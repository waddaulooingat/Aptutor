using Amazon;
using Amazon.S3;
using ApTutor.Client.Courses;
using ApTutor.Client.Services;
using ApTutor.Content;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;
using ApTutor.Platform;
using ApTutor.Scene;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ApTutor.Client;

public partial class MainWindow : Window
{
    private readonly CourseRegistry _registry = new();
    private readonly ProgressStore _progressStore = new();
    private readonly Dictionary<string, NodeItemVm> _nodeVms = new(StringComparer.Ordinal);

    private ICourseModule _course = null!;
    private MasteryTracker _mastery = null!;
    private DagNode? _selectedNode;
    private bool _refreshingContent;

    public MainWindow()
    {
        InitializeComponent();
        LoadCourses();
    }

    /// Registers every course the shell knows about and wires the dropdown to switch between
    /// them. Defaults to CS A on launch — not persisted across runs (a "remember last course"
    /// feature is a reasonable follow-up, not required yet).
    private void LoadCourses()
    {
        var dagDir = AppContext.BaseDirectory;
        var csa = new CsaCourseModule(Path.Combine(dagDir, "apcsa-skill-dag.json"));
        var worldHistory = new WorldHistoryCourseModule(Path.Combine(dagDir, "apwh-skill-dag.json"));
        _registry.Register(csa);
        _registry.Register(worldHistory);

        CourseSelector.ItemsSource = _registry.All;
        CourseSelector.SelectedItem = csa; // triggers OnCourseSelectionChanged -> SwitchCourse
    }

    private void OnCourseSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CourseSelector.SelectedItem is ICourseModule course && course != _course)
            SwitchCourse(course);
    }

    private void SwitchCourse(ICourseModule course)
    {
        _course = course;
        _mastery = LoadMasteryFor(course);
        _nodeVms.Clear();
        _selectedNode = null;
        Title = $"Tutor AI — {course.DisplayName}";

        BuildTree();
        UpdateActionButtonAvailability();
        RefreshAll();
    }

    private MasteryTracker LoadMasteryFor(ICourseModule course)
    {
        var mastery = new MasteryTracker(course.Dag);
        var saved = _progressStore.Load();
        if (saved.TryGetValue(course.CourseId, out var masteredIds))
            foreach (var id in masteredIds)
                if (course.Dag.Exists(id))
                    mastery.MarkMastered(id);
        return mastery;
    }

    /// MockExamSession throws if a course has zero practice items anywhere (true for World History
    /// until someone runs ApTutor.ContentFactory for it) — disable rather than let it crash on click.
    private void UpdateActionButtonAvailability()
    {
        MockExamButton.IsEnabled = _course.Dag.Dag.Nodes.Any(n => _course.Content.GetPracticeItems(n.Id).Count > 0);
    }

    private void RefreshAll()
    {
        UpdateNodeStates();
        RefreshProgress();
        RefreshDetail();
    }

    /// Builds the tree's ItemsSource exactly once. NodeItemVm instances are cached by node id and
    /// updated in place afterward (UpdateNodeStates) instead of the tree being torn down and
    /// rebuilt on every mastery change — rebuilding replaced every VM with a new instance, which
    /// collapsed the user's tree expansion back to the top level on every "Mark mastered" click.
    private void BuildTree()
    {
        var available = _mastery.Available().Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        var groups = _course.Dag.Dag.Units
            .OrderBy(u => u.Unit)
            .Select(u => new UnitGroupVm(
                u.Unit,
                u.Title,
                _course.Dag.Dag.Nodes
                    .Where(n => n.Unit == u.Unit)
                    .OrderBy(n => n.Id, StringComparer.Ordinal)
                    .Select(n =>
                    {
                        var vm = new NodeItemVm(n, _mastery.IsMastered(n.Id), available.Contains(n.Id));
                        _nodeVms[n.Id] = vm;
                        return vm;
                    })
                    .ToList()))
            .ToList();

        UnitTree.ItemsSource = groups;
    }

    /// Folded "Available now" straight into the tree: available = unlocked (prereqs mastered) but
    /// not yet mastered itself — NodeItemVm renders that as a distinct marker/color. Updates the
    /// existing VM instances in place (see BuildTree) so expansion state survives.
    private void UpdateNodeStates()
    {
        var available = _mastery.Available().Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, vm) in _nodeVms)
            vm.SetState(_mastery.IsMastered(id), available.Contains(id));
    }

    private void RefreshProgress()
    {
        var pct = _mastery.PercentComplete * 100.0;
        MasteryProgressBar.Value = pct;
        ProgressLabel.Text = $"{_mastery.Mastered.Count} / {_course.Dag.Dag.Nodes.Count} mastered ({pct:0.#}%)";
    }

    private void RefreshDetail()
    {
        ContentPanel.Children.Clear();

        if (_selectedNode is not { } node)
        {
            DetailTitle.Text = "Select a node";
            DetailType.Text = string.Empty;
            DetailPrereqs.Text = string.Empty;
            DetailViz.Text = string.Empty;
            MarkMasteredButton.IsEnabled = false;
            return;
        }

        DetailTitle.Text = $"{node.Id} — {node.Title}";
        DetailType.Text = $"Type: {node.Type}";
        DetailPrereqs.Text = node.Prereqs.Count == 0
            ? "Prereqs: none"
            : $"Prereqs: {string.Join(", ", node.Prereqs)}";
        DetailViz.Text = $"Viz: {node.Viz}";
        MarkMasteredButton.IsEnabled = !_mastery.IsMastered(node.Id);

        RefreshContent(node);
    }

    /// Phase 7 follow-up: surfaces whatever's actually in the generated/verified content pack for
    /// the selected node (walkthrough text + practice items) — the detail pane previously only
    /// showed the DAG's own structural fields (Type/Prereqs/Viz), never the generated content
    /// itself, which made it look like "generate" wasn't doing anything even when it was.
    private void RefreshContent(DagNode node)
    {
        string? walkthroughText = null;
        try
        {
            walkthroughText = _course.Content.GetWalkthroughText(node.Id, "generated");
        }
        catch
        {
            // No verified walkthrough text for this node yet — shown as a placeholder below.
        }

        ContentPanel.Children.Add(new TextBlock
        {
            Text = walkthroughText ?? "(no walkthrough text generated + verified for this node yet)",
            TextWrapping = TextWrapping.Wrap,
            FontStyle = walkthroughText is null ? FontStyle.Italic : FontStyle.Normal,
            Foreground = walkthroughText is null ? Brushes.Gray : Brushes.Black,
        });

        // Dev-only "Refresh questions" (see OnRefreshQuestionsClick) writes a fresh, Verified: false
        // pack straight to disk, bypassing the normal verified-only Content.GetPracticeItems path on
        // purpose — peek at the raw pack here so a refresh is actually visible, clearly labeled as
        // unreviewed rather than silently invisible until someone runs the review CLI.
        var rawPack = ContentPackStore.TryLoad(_course.ContentDir, node.Id);
        if (rawPack is { Verified: false })
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = $"⚠ AI-generated, unverified (refreshed {rawPack.GeneratedAt:g}) — double-check before trusting. Run 'review' to approve.",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.DarkOrange,
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (var item in rawPack.PracticeItems)
                ContentPanel.Children.Add(BuildPracticeItemBlock(item));
            return;
        }

        var items = _course.Content.GetPracticeItems(node.Id);
        if (items.Count == 0)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "(no practice items for this node yet)",
                FontStyle = FontStyle.Italic,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }

        foreach (var item in items)
            ContentPanel.Children.Add(BuildPracticeItemBlock(item));
    }

    /// Dev-only: right-click a node -> regenerate JUST its practice items live via Claude, saved as
    /// an unverified pack (see RefreshContent's warning banner). Never touches Mock Exam's
    /// verified-only serving path — a refreshed-but-unreviewed item is only ever visible here in
    /// the detail pane, clearly labeled, until a human runs `review` and approves it.
    private async void OnRefreshQuestionsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: NodeItemVm vm }) return;
        var node = vm.Node;

        _selectedNode = node;
        RefreshDetail();

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var model = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            ShowContentMessage("⚠ Set ANTHROPIC_API_KEY and ANTHROPIC_MODEL to use Refresh questions (dev-only).", Brushes.DarkOrange);
            return;
        }

        ShowContentMessage($"⏳ Refreshing questions for {node.Id} via Claude ({model})...", Brushes.Gray);

        try
        {
            var generator = new Generator(new ClaudeClient(apiKey, model));
            var newItems = await generator.RegeneratePracticeItemsAsync(_course.CourseId, node);

            var existing = ContentPackStore.TryLoad(_course.ContentDir, node.Id);
            var pack = existing is { } e2
                ? e2 with { PracticeItems = newItems, Verified = false, GeneratedAt = DateTimeOffset.UtcNow, Model = model }
                : new NodeContentPack(_course.CourseId, node.Id, "generated", "", newItems, Array.Empty<VisualStep>(), false, DateTimeOffset.UtcNow, model);

            ContentPackStore.Save(_course.ContentDir, pack);

            if (_selectedNode?.Id == node.Id) RefreshDetail();
        }
        catch (Exception ex)
        {
            ShowContentMessage($"⚠ Refresh failed: {ex.Message}", Brushes.DarkRed);
        }
    }

    /// Minimal, temporary test bridge (see ApTutor.Client.Services.ContentSyncService) — pulls
    /// whatever's currently approved for the selected course straight from S3 with a read-only
    /// credential. Not the shipping design: a Licensing/entitlement service issuing short-lived
    /// signed URLs is meant to sit in front of this before any real customer's install talks to S3
    /// directly. Refreshes only the currently-selected course.
    private async void OnRefreshContentClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshingContent) return;

        var bucket = Environment.GetEnvironmentVariable("TUTORAI_CONTENT_BUCKET");
        var region = Environment.GetEnvironmentVariable("TUTORAI_CONTENT_REGION");
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(region))
        {
            ShowContentMessage("⚠ Set TUTORAI_CONTENT_BUCKET and TUTORAI_CONTENT_REGION to use Refresh content.", Brushes.DarkOrange);
            return;
        }

        _refreshingContent = true;
        RefreshContentButton.IsEnabled = false;
        var course = _course; // capture: the course selector could change while this await is in flight
        ShowContentMessage($"⏳ Refreshing {course.DisplayName} content...", Brushes.Gray);

        try
        {
            using var s3 = new AmazonS3Client(new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) });
            var sync = new ContentSyncService(s3, bucket);
            var result = await sync.RefreshAsync(course);

            if (!result.Success)
            {
                ShowContentMessage(result.UserMessage!, Brushes.DarkRed);
            }
            else if (course == _course) // still on the same course — safe to refresh the tree/detail
            {
                RefreshAll();
                ShowContentMessage(
                    result.UpdatedCount == 0 ? "Already up to date." : $"Updated {result.UpdatedCount} topic(s).",
                    Brushes.DarkGreen);
            }
        }
        catch (Exception ex)
        {
            // ContentSyncService.RefreshAsync already catches everything it can throw — this is a
            // backstop for anything before/around it (e.g. constructing the S3 client itself), so a
            // misconfiguration here can't crash an async void handler.
            Console.Error.WriteLine($"[OnRefreshContentClick] {ex}");
            ShowContentMessage("You are offline. This app needs an internet connection. Til then you can review the previously downloaded content.", Brushes.DarkRed);
        }
        finally
        {
            _refreshingContent = false;
            RefreshContentButton.IsEnabled = true;
        }
    }

    private void ShowContentMessage(string text, IBrush color)
    {
        ContentPanel.Children.Clear();
        ContentPanel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = color });
    }

    /// Practice items used to render with the correct choice already marked (✓) — fine for the
    /// content factory's own review CLI, but this panel is user-facing, and showing the answer
    /// before the student has even read the question defeats the entire point of a practice item.
    /// Now: pick a choice, hit "Check answer", THEN see correct/incorrect + the explanation —
    /// mirrors how MockExamWindow already withholds the answer until a question is submitted.
    private static Control BuildPracticeItemBlock(PracticeItem item)
    {
        var container = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        container.Children.Add(new TextBlock
        {
            Text = $"Q: {item.Prompt}",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });

        int? selected = null;
        var radios = new List<RadioButton>();
        for (var i = 0; i < item.Choices.Count; i++)
        {
            var choiceIndex = i;
            var radio = new RadioButton { Content = item.Choices[i], GroupName = item.Id };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true) selected = choiceIndex;
            };
            radios.Add(radio);
            container.Children.Add(radio);
        }

        var resultText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        var checkButton = new Button { Content = "Check answer" };
        checkButton.Click += (_, _) =>
        {
            if (selected is not { } chosen) return;

            foreach (var radio in radios) radio.IsEnabled = false;
            checkButton.IsEnabled = false;

            var correct = chosen == item.CorrectIndex;
            resultText.Text = correct
                ? $"✓ Correct! {item.Explanation}"
                : $"✗ Not quite — correct answer: {(char)('A' + item.CorrectIndex)}. {item.Choices[item.CorrectIndex]}\n{item.Explanation}";
            resultText.Foreground = correct ? Brushes.DarkGreen : Brushes.DarkRed;
            resultText.IsVisible = true;
        };

        container.Children.Add(checkButton);
        container.Children.Add(resultText);
        return container;
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (UnitTree.SelectedItem is NodeItemVm vm)
        {
            _selectedNode = vm.Node;
            RefreshDetail();
        }
    }

    private void OnMarkMasteredClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedNode is not { } node) return;

        _mastery.MarkMastered(node.Id);
        SaveProgress();
        RefreshAll();
    }

    private void OnMockExamClick(object? sender, RoutedEventArgs e) =>
        new MockExamWindow(_course, _mastery, () => { SaveProgress(); RefreshAll(); }).Show();

    private void SaveProgress()
    {
        var mutable = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var (courseId, ids) in _progressStore.Load())
            mutable[courseId] = ids;
        mutable[_course.CourseId] = _mastery.Mastered.ToList();

        _progressStore.Save(mutable);
    }
}
