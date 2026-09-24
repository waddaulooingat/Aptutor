// Phase 6: timed mock exam UI. Pulls every practice item across the course's DAG nodes (via
// IContentSource — course-agnostic, no CS A-specific code here), runs a MockExamSession with a
// live countdown, and on submit (or timeout) applies weak nodes back onto the shared
// MasteryTracker so they resurface in the frontier — then hands control back to the caller
// (MainWindow) via onFinished so it can persist progress and refresh the shell.

using ApTutor.Client.Services;
using ApTutor.Curriculum;
using ApTutor.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace ApTutor.Client;

public partial class MockExamWindow : Window
{
    private readonly MockExamSession _session;
    private readonly MasteryTracker _mastery;
    private readonly Action _onFinished;
    private readonly DispatcherTimer _timer;
    private int _currentIndex;
    private int? _selectedForCurrent;

    public MockExamWindow(ICourseModule course, MasteryTracker mastery, Action onFinished)
    {
        InitializeComponent();
        _mastery = mastery;
        _onFinished = onFinished;

        var items = course.Dag.Dag.Nodes.SelectMany(n => course.Content.GetPracticeItems(n.Id)).ToList();
        var duration = TimeSpan.FromSeconds(Math.Max(60, items.Count * 60));
        _session = new MockExamSession(items, duration);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        ShowQuestion(0);
    }

    /// A graph-typed choice has no renderer yet (see the graph-spec-rendering plan) — shown as a
    /// clearly-labeled placeholder rather than falling back to the record's default ToString().
    private static string ChoiceDisplayText(PracticeItemChoice choice) =>
        choice.Text ?? "[Graph-based answer — rendering not built yet]";

    private void ShowQuestion(int index)
    {
        _currentIndex = index;
        _selectedForCurrent = null;
        var item = _session.Items[index];

        ProgressText.Text = $"Question {index + 1} / {_session.Items.Count}";
        PromptContainer.Content = MathTextRenderer.Build(item.Prompt, fontSize: 16);

        ChoicesPanel.Children.Clear();
        for (var i = 0; i < item.Choices.Count; i++)
        {
            var choiceIndex = i;
            var radio = new RadioButton { Content = MathTextRenderer.Build(ChoiceDisplayText(item.Choices[i])), GroupName = "mockExamChoices" };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true) _selectedForCurrent = choiceIndex;
            };
            ChoicesPanel.Children.Add(radio);
        }

        NextButton.Content = index == _session.Items.Count - 1 ? "Submit" : "Next";
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        TimerText.Text = $"Time left: {_session.Remaining:mm\\:ss}";
        if (_session.IsTimedOut) Finish();
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedForCurrent is { } choiceIndex)
            _session.Answer(_session.Items[_currentIndex].Id, choiceIndex);

        if (_currentIndex + 1 < _session.Items.Count)
            ShowQuestion(_currentIndex + 1);
        else
            Finish();
    }

    private void Finish()
    {
        if (_session.IsSubmitted) return;

        _timer.Stop();
        var result = _session.Submit();
        result.ApplyWeakNodes(_mastery);
        _onFinished();
        ShowResults(result);
    }

    private void ShowResults(MockExamResult result)
    {
        ExamPanel.IsVisible = false;
        ResultsPanel.IsVisible = true;
        NextButton.IsVisible = false;

        ProgressText.Text = $"Score: {result.CorrectCount} / {result.TotalCount} ({result.PercentCorrect:P0})";
        TimerText.Text = result.TimedOut ? "Time expired" : $"Completed in {result.Elapsed:mm\\:ss}";

        ResultsList.Children.Clear();

        if (result.WeakNodeIds.Count > 0)
        {
            var flagged = MathTextRenderer.Build($"Flagged for review: {string.Join(", ", result.WeakNodeIds)}", fontWeight: FontWeight.Bold);
            flagged.Margin = new Thickness(0, 0, 0, 12);
            ResultsList.Children.Add(flagged);
        }

        // Each result is rendered line-by-line (rather than one multi-line string) because
        // MathTextRenderer.Build lays a line's $...$ segments out in a horizontal WrapPanel, which
        // doesn't understand embedded "\n" the way a plain TextBlock did.
        foreach (var item in result.Items)
        {
            var container = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
            container.Children.Add(MathTextRenderer.Build(item.Correct ? $"✓ {item.Item.Prompt}" : $"✗ {item.Item.Prompt}"));

            if (!item.Correct)
            {
                var yourAnswer = item.SelectedIndex is { } si ? ChoiceDisplayText(item.Item.Choices[si]) : "(unanswered)";
                container.Children.Add(MathTextRenderer.Build($"   Your answer: {yourAnswer}"));
                container.Children.Add(MathTextRenderer.Build($"   Correct answer: {ChoiceDisplayText(item.Item.Choices[item.Item.CorrectIndex])}"));
                container.Children.Add(MathTextRenderer.Build($"   {item.Item.Explanation}"));
            }

            ResultsList.Children.Add(container);
        }
    }
}
