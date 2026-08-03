using ApTutor.Client.Courses;
using ApTutor.Curriculum;
using ApTutor.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ApTutor.Client;

public partial class MainWindow : Window
{
    private readonly CourseRegistry _registry = new();
    private readonly ProgressStore _progressStore = new();

    private ICourseModule _course = null!;
    private MasteryTracker _mastery = null!;
    private DagNode? _selectedNode;

    public MainWindow()
    {
        InitializeComponent();
        LoadCourse();
        RefreshAll();
    }

    private void LoadCourse()
    {
        var dagPath = Path.Combine(AppContext.BaseDirectory, "apcsa-skill-dag.json");
        var csa = new CsaCourseModule(dagPath);
        _registry.Register(csa);
        _course = _registry.Get(csa.CourseId);
        _mastery = new MasteryTracker(_course.Dag);

        var saved = _progressStore.Load();
        if (saved.TryGetValue(_course.CourseId, out var masteredIds))
            foreach (var id in masteredIds)
                if (_course.Dag.Exists(id))
                    _mastery.MarkMastered(id);
    }

    private void RefreshAll()
    {
        RefreshTree();
        RefreshFrontier();
        RefreshProgress();
        RefreshDetail();
    }

    private void RefreshTree()
    {
        var groups = _course.Dag.Dag.Units
            .OrderBy(u => u.Unit)
            .Select(u => new UnitGroupVm(
                u.Unit,
                u.Title,
                _course.Dag.Dag.Nodes
                    .Where(n => n.Unit == u.Unit)
                    .OrderBy(n => n.Id, StringComparer.Ordinal)
                    .Select(n => new NodeItemVm(n, _mastery.IsMastered(n.Id)))
                    .ToList()))
            .ToList();

        UnitTree.ItemsSource = groups;
    }

    private void RefreshFrontier()
    {
        FrontierList.ItemsSource = _mastery.Available()
            .Select(n => new NodeItemVm(n, false))
            .ToList();
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
        {
            var block = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
            block.Children.Add(new TextBlock
            {
                Text = $"Q: {item.Prompt}",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeight.SemiBold,
            });
            for (var i = 0; i < item.Choices.Count; i++)
            {
                block.Children.Add(new TextBlock
                {
                    Text = $"{(i == item.CorrectIndex ? "✓" : " ")} {(char)('A' + i)}. {item.Choices[i]}",
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            block.Children.Add(new TextBlock
            {
                Text = $"Explanation: {item.Explanation}",
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyle.Italic,
                Foreground = Brushes.Gray,
            });
            ContentPanel.Children.Add(block);
        }
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (UnitTree.SelectedItem is NodeItemVm vm)
        {
            _selectedNode = vm.Node;
            RefreshDetail();
        }
    }

    private void OnFrontierSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FrontierList.SelectedItem is NodeItemVm vm)
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

    private void OnSceneDemoClick(object? sender, RoutedEventArgs e) => new SceneDemoWindow(_course).Show();

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
