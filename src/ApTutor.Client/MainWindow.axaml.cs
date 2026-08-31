using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using ApTutor.Client.Courses;
using ApTutor.Client.Services;
using ApTutor.Content;
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
    private AppSettings _settings = AppSettingsStore.Load(AppSettingsStore.DefaultDir);

    // Local, per-installation student performance state (see the difficulty-and-progress-tracking
    // plan's Part B) — never synced to S3, never seen by Content Admin. _selectedDifficulty starts
    // at Medium deliberately (not the enum's default Easy) to match today's practice-item baseline.
    private Difficulty _selectedDifficulty = Difficulty.Medium;
    private bool _retryMode;

    public MainWindow()
    {
        InitializeComponent();
        DifficultySelector.ItemsSource = new[] { Difficulty.Easy, Difficulty.Medium, Difficulty.Hard };
        DifficultySelector.SelectedItem = _selectedDifficulty;
        _ = LoadCoursesAsync();
    }

    /// Reloads settings after the dialog closes — Save or Cancel both just close the window, so
    /// re-reading from disk (a no-op on Cancel, since nothing was written) is simpler than plumbing
    /// a result back out of it.
    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        await new SettingsWindow().ShowDialog(this);
        _settings = AppSettingsStore.Load(AppSettingsStore.DefaultDir);
    }

    /// Discovers every course from S3 (see CourseDiscoveryService) and registers an ICourseModule
    /// per one — no hardcoded course list, no baked-in DAG file. CS A is the one deliberate
    /// exception: it keeps its own bespoke wiring (the live Java tracer, the fixture item bank via
    /// CsaCourseModule) since that's real interactive engineering no generator can produce (see the
    /// Shell-display-only/course-authoring plan); every other course gets the generic,
    /// course-agnostic wiring (GenericCourseModule). Defaults to CS A on launch when present, else
    /// whatever course sorts first — not persisted across runs.
    ///
    /// This is genuinely async now (course structure lives in S3, not next to the exe), which is a
    /// real behavior change from before: the window is briefly non-interactive on first load, and
    /// the mastery/practice UI has nothing to show at all if this machine has never reached S3
    /// before. Course-picking controls are explicitly disabled for that window so nothing can run
    /// against a not-yet-set _course.
    private async Task LoadCoursesAsync()
    {
        CourseSelector.IsEnabled = false;
        MockExamButton.IsEnabled = false;
        ProgressChartButton.IsEnabled = false;
        RefreshContentButton.IsEnabled = false;
        DetailTitle.Text = "Loading courses…";

        if (!TryResolveS3Config(out var bucket, out var region, out var configError))
        {
            DetailTitle.Text = "Could not load courses.";
            ShowContentMessage($"{configError} Then restart the app.", Brushes.DarkOrange);
            return;
        }

        try
        {
            using var s3 = BuildS3Client(region);
            var discovered = await new CourseDiscoveryService(s3, bucket).DiscoverCoursesAsync();

            if (discovered.Count == 0)
            {
                DetailTitle.Text = "No courses are available yet.";
                ShowContentMessage("Check back soon.", Brushes.Gray);
                return;
            }

            foreach (var course in discovered)
            {
                var contentDir = Path.Combine(AppContext.BaseDirectory, "content", course.CourseId);
                ICourseModule module = course.CourseId == "csa"
                    ? new CsaCourseModule(course.Graph, contentDir)
                    : new GenericCourseModule(
                        course.CourseId,
                        course.Graph.Dag.Meta.DisplayName ?? course.Graph.Dag.Meta.Course,
                        course.Graph,
                        contentDir);
                _registry.Register(module);
            }

            CourseSelector.ItemsSource = _registry.All;
            CourseSelector.IsEnabled = true;
            ProgressChartButton.IsEnabled = true;
            RefreshContentButton.IsEnabled = true;

            var defaultCourse = _registry.All.FirstOrDefault(c => c.CourseId == "csa") ?? _registry.All.First();
            CourseSelector.SelectedItem = defaultCourse; // triggers OnCourseSelectionChanged -> SwitchCourse
        }
        catch (Exception ex)
        {
            // Same posture as ContentSyncService/OnRefreshContentClick: never show a raw exception
            // to a student, log it for anyone actually debugging this.
            Console.Error.WriteLine($"[LoadCoursesAsync] {ex}");
            DetailTitle.Text = "You are offline.";
            ShowContentMessage("This app needs an internet connection to load courses for the first time. Try again once you're back online.", Brushes.DarkRed);
        }
    }

    /// Resolves bucket/region the same way for both course discovery and content refresh — false
    /// with a user-facing message if either isn't configured at all (nothing sensible to try
    /// without them).
    private bool TryResolveS3Config(out string bucket, out string region, out string error)
    {
        var resolvedBucket = ConfigResolver.Resolve("TUTORAI_CONTENT_BUCKET", _settings.ContentBucket);
        var resolvedRegion = ConfigResolver.Resolve("TUTORAI_CONTENT_REGION", _settings.ContentRegion);
        if (string.IsNullOrWhiteSpace(resolvedBucket) || string.IsNullOrWhiteSpace(resolvedRegion))
        {
            bucket = "";
            region = "";
            error = "⚠ Set the content bucket + region in Settings first.";
            return false;
        }

        bucket = resolvedBucket;
        region = resolvedRegion;
        error = "";
        return true;
    }

    /// A Settings-saved credential isn't something the SDK's own resolution chain (env vars, shared
    /// config file, instance role, ...) would ever find on its own, so it's passed explicitly when
    /// present; otherwise falls through to that chain unchanged — e.g. an AWS_* env var, or an
    /// EC2/App Runner instance role.
    private AmazonS3Client BuildS3Client(string region)
    {
        var accessKeyId = ConfigResolver.Resolve("AWS_ACCESS_KEY_ID", _settings.AwsAccessKeyId);
        var secretAccessKey = ConfigResolver.Resolve("AWS_SECRET_ACCESS_KEY", _settings.AwsSecretAccessKey);
        var s3Config = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
        return !string.IsNullOrWhiteSpace(accessKeyId) && !string.IsNullOrWhiteSpace(secretAccessKey)
            ? new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), s3Config)
            : new AmazonS3Client(s3Config);
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
        _retryMode = false;
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
            RetryWrongButton.IsEnabled = false;
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
            // Unreviewed dev preview content — deliberately outside the difficulty picker/retry/
            // attempt-log system below, same as before: this is a raw peek at a just-refreshed
            // draft, not real student practice.
            foreach (var item in rawPack.PracticeItems)
                ContentPanel.Children.Add(BuildPreviewPracticeItemBlock(item));
            return;
        }

        UpdateRetryButtonState(node);

        if (_retryMode)
        {
            RenderRetryQuestions(node);
            return;
        }

        var items = GetPracticeItemsForDifficulty(node.Id, _selectedDifficulty);
        if (items.Count == 0)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = $"(no {_selectedDifficulty.ToString().ToLowerInvariant()} practice items for this node yet)",
                FontStyle = FontStyle.Italic,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }

        foreach (var scoped in items)
            ContentPanel.Children.Add(BuildPracticeItemBlock(node, scoped.PackHash, scoped.Item, _selectedDifficulty));
    }

    /// One node can have several independently-approved sets at the chosen difficulty (see the
    /// multi-set library plan) — same "pick one set at random per visit" convention
    /// FileContentSource already uses for its own (difficulty-agnostic) selection, so practice
    /// varies across sessions without overwhelming the panel with every set at once. PackHash rides
    /// along per item so an answered attempt can be logged (and later retried) against the exact
    /// set it came from — see AttemptRecord's remarks on why a bare item id isn't enough.
    private sealed record ScopedPracticeItem(string PackHash, PracticeItem Item);

    private IReadOnlyList<ScopedPracticeItem> GetPracticeItemsForDifficulty(string nodeId, Difficulty difficulty)
    {
        var candidates = ContentPackStore.LoadVersions(_course.ContentDir, nodeId)
            .Where(p => p.Verified && p.Difficulty == difficulty)
            .ToList();
        if (candidates.Count == 0) return Array.Empty<ScopedPracticeItem>();

        var chosen = candidates[Random.Shared.Next(candidates.Count)];
        var hash = ContentHash.Compute(chosen);
        return chosen.PracticeItems.Select(i => new ScopedPracticeItem(hash, i)).ToList();
    }

    /// Resolves a specific, previously-answered question back to its exact PracticeItem for retry —
    /// null if that particular set is no longer cached locally (e.g. it was superseded and never
    /// re-synced), in which case the caller just skips it rather than crashing.
    private PracticeItem? ResolvePracticeItem(string nodeId, string packHash, string questionId) =>
        ContentPackStore.LoadVersions(_course.ContentDir, nodeId)
            .FirstOrDefault(p => ContentHash.Compute(p) == packHash)
            ?.PracticeItems.FirstOrDefault(i => i.Id == questionId);

    /// "Retry wrong questions" (see the difficulty-and-progress-tracking plan's Part B) — replays
    /// the EXACT items the student most recently missed on this node, across whichever difficulty
    /// each one was originally attempted at, not a fresh difficulty-scoped draw. Answering one
    /// correctly resolves it (AttemptAnalysis derives "unresolved" from the latest attempt per
    /// question, not a mutable flag) — reopening this view afterward naturally drops it from the list.
    private void RenderRetryQuestions(DagNode node)
    {
        var backButton = new Button { Content = "← Back to practice" };
        backButton.Click += (_, _) =>
        {
            _retryMode = false;
            RefreshDetail();
        };
        ContentPanel.Children.Add(backButton);

        var attempts = AttemptLogStore.LoadAll(AttemptLogStore.DefaultDir);
        var unresolved = AttemptAnalysis.UnresolvedWrongQuestions(attempts, _course.CourseId, node.Id);

        if (unresolved.Count == 0)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "Nothing left to retry for this topic — nice work.",
                Foreground = Brushes.DarkGreen,
                Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }

        foreach (var q in unresolved)
        {
            var item = ResolvePracticeItem(node.Id, q.PackHash, q.QuestionId);
            if (item is null) continue; // that specific set isn't cached locally anymore
            ContentPanel.Children.Add(BuildPracticeItemBlock(node, q.PackHash, item, q.Difficulty));
        }
    }

    private void UpdateRetryButtonState(DagNode node)
    {
        var attempts = AttemptLogStore.LoadAll(AttemptLogStore.DefaultDir);
        var unresolvedCount = AttemptAnalysis.UnresolvedWrongQuestions(attempts, _course.CourseId, node.Id).Count;
        RetryWrongButton.IsEnabled = unresolvedCount > 0;
        RetryWrongButton.Content = unresolvedCount > 0 ? $"Retry wrong questions ({unresolvedCount})" : "Retry wrong questions";
    }

    /// Right-click a node -> pulls that node's latest approved set from S3, if there's anything new
    /// — always S3, never Claude (see the Shell-display-only/course-authoring plan's Part E, which
    /// retired the old live-Claude-backed dev tool this used to be: all content generation happens
    /// exclusively in Content Admin now, with SME review, and the Shell only ever displays what was
    /// approved there). Same underlying mechanism as the course-level "Refresh content" button
    /// (ContentSyncService.RefreshNodeAsync), just scoped to one node.
    private async void OnGetNewSetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: NodeItemVm vm }) return;
        var node = vm.Node;

        _selectedNode = node;
        RefreshDetail();

        if (!TryResolveS3Config(out var bucket, out var region, out var configError))
        {
            ShowContentMessage(configError, Brushes.DarkOrange);
            return;
        }

        ShowContentMessage($"⏳ Checking for new content for {node.Id}...", Brushes.Gray);

        try
        {
            using var s3 = BuildS3Client(region);
            var sync = new ContentSyncService(s3, bucket);
            var result = await sync.RefreshNodeAsync(_course, node.Id);

            if (!result.Success)
            {
                ShowContentMessage(result.UserMessage!, Brushes.DarkRed);
            }
            else if (result.UpdatedNodeIds.Count == 0)
            {
                ShowContentMessage("More content coming.", Brushes.Gray);
            }
            else
            {
                if (_selectedNode?.Id == node.Id) RefreshDetail();
                ShowContentMessage($"Pulled new content for {node.Id}.", Brushes.DarkGreen);
            }
        }
        catch (Exception ex)
        {
            // ContentSyncService.RefreshNodeAsync already catches everything it can throw — this is
            // a backstop for anything before/around it, same posture as OnRefreshContentClick.
            Console.Error.WriteLine($"[OnGetNewSetClick] {ex}");
            ShowContentMessage("You are offline. This app needs an internet connection. Til then you can review the previously downloaded content.", Brushes.DarkRed);
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

        if (!TryResolveS3Config(out var bucket, out var region, out var configError))
        {
            ShowContentMessage($"{configError} (needed for Refresh content).", Brushes.DarkOrange);
            return;
        }

        _refreshingContent = true;
        RefreshContentButton.IsEnabled = false;
        var course = _course; // capture: the course selector could change while this await is in flight
        ShowContentMessage($"⏳ Refreshing {course.DisplayName} content...", Brushes.Gray);

        try
        {
            using var s3 = BuildS3Client(region);
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
                    result.UpdatedNodeIds.Count == 0
                        ? "No new units available. Check back soon."
                        : $"Pulled from S3: {string.Join(", ", result.UpdatedNodeIds)}",
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

    /// Dev-only raw preview of an unreviewed, just-refreshed draft (see RefreshContent's
    /// rawPack-is-unverified branch) — same withhold-the-answer-until-checked UX as the real
    /// tracked version below, but deliberately does NOT log an AttemptRecord: this is a peek at
    /// content nobody has approved yet, not real student practice.
    private static Control BuildPreviewPracticeItemBlock(PracticeItem item)
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

    /// Real, tracked practice — same withhold-the-answer-until-checked UX as
    /// BuildPreviewPracticeItemBlock, but every answer is logged locally (see AttemptLogStore),
    /// which is what feeds the retry-wrong-questions and progress-chart features. GroupName is
    /// scoped by packHash, not just the item id, since the retry view can show two questions that
    /// happen to share an id from two different approved sets at once (see AttemptRecord's remarks)
    /// — RadioButton's mutual-exclusion grouping needs those kept visually separate too.
    private Control BuildPracticeItemBlock(DagNode node, string packHash, PracticeItem item, Difficulty difficulty)
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
            var radio = new RadioButton { Content = item.Choices[i], GroupName = $"{packHash}-{item.Id}" };
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

            AttemptLogStore.Append(AttemptLogStore.DefaultDir, new AttemptRecord(
                DateTimeOffset.UtcNow, _course.CourseId, node.Id, difficulty, packHash, item.Id, correct));
            UpdateRetryButtonState(node);
        };

        container.Children.Add(checkButton);
        container.Children.Add(resultText);
        return container;
    }

    private void OnDifficultySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DifficultySelector.SelectedItem is not Difficulty difficulty || difficulty == _selectedDifficulty) return;

        _selectedDifficulty = difficulty;
        _retryMode = false;
        if (_selectedNode is not null) RefreshDetail();
    }

    private void OnRetryWrongClick(object? sender, RoutedEventArgs e)
    {
        _retryMode = true;
        RefreshDetail();
    }

    /// Scoped to the currently-selected course, same as every other data-pulling action in this
    /// window (Refresh content, Mock Exam) — there's no cross-course rollup here, matching how the
    /// rest of the Shell treats "the selected course" as the unit of scope throughout.
    private void OnProgressChartClick(object? sender, RoutedEventArgs e)
    {
        var attempts = AttemptLogStore.LoadAll(AttemptLogStore.DefaultDir);
        var weeks = AttemptAnalysis.WeeklyAccuracyForCourse(attempts, _course.CourseId, weekCount: 8);
        new ProgressChartWindow(_course.DisplayName, weeks).Show();
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (UnitTree.SelectedItem is NodeItemVm vm)
        {
            _selectedNode = vm.Node;
            _retryMode = false;
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
