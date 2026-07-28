// Advanced Test Prepper — Phase 6: timed mock exam engine (MCQ only; FRQ mode is a documented
// stub until the local FRQ sandbox exists — see build-plan.md Phase 5/13 notes).
// Course-agnostic: works off IContentSource.GetPracticeItems, so it needs no CS A-specific code.

using ApTutor.Curriculum;

namespace ApTutor.Platform;

public sealed record ExamItemResult(PracticeItem Item, int? SelectedIndex, bool Correct);

public sealed record MockExamResult(
    IReadOnlyList<ExamItemResult> Items, int CorrectCount, int TotalCount, TimeSpan Elapsed, bool TimedOut)
{
    public double PercentCorrect => TotalCount == 0 ? 0 : (double)CorrectCount / TotalCount;

    /// Nodes behind at least one missed item — the mock exam's signal that mastery wasn't real.
    public IReadOnlyList<string> WeakNodeIds =>
        Items.Where(i => !i.Correct).Select(i => i.Item.NodeId).Distinct().ToList();

    /// Un-masters every weak node so it resurfaces in MasteryTracker.Available() for review.
    public void ApplyWeakNodes(MasteryTracker tracker)
    {
        foreach (var nodeId in WeakNodeIds)
            tracker.MarkWeak(nodeId);
    }
}

/// One timed attempt at a fixed item set. Answer() records a choice; Submit() grades everything
/// answered-or-not (unanswered = incorrect) and stops the clock. The clock is injectable so tests
/// don't depend on wall time.
public sealed class MockExamSession
{
    private readonly Dictionary<string, int> _answers = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;
    private readonly DateTimeOffset _start;

    public IReadOnlyList<PracticeItem> Items { get; }
    public TimeSpan Duration { get; }
    public bool IsSubmitted { get; private set; }

    public MockExamSession(IReadOnlyList<PracticeItem> items, TimeSpan duration, Func<DateTimeOffset>? nowProvider = null)
    {
        if (items.Count == 0) throw new ArgumentException("A mock exam needs at least one item.", nameof(items));
        Items = items;
        Duration = duration;
        _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
        _start = _now();
    }

    public TimeSpan Elapsed => _now() - _start;
    public TimeSpan Remaining => Duration - Elapsed is var r && r > TimeSpan.Zero ? r : TimeSpan.Zero;
    public bool IsTimedOut => Elapsed >= Duration;

    public void Answer(string itemId, int selectedIndex)
    {
        if (IsSubmitted) throw new InvalidOperationException("This exam has already been submitted.");
        if (IsTimedOut) throw new InvalidOperationException("Exam time has expired.");
        if (Items.All(i => i.Id != itemId)) throw new KeyNotFoundException(itemId);
        _answers[itemId] = selectedIndex;
    }

    public MockExamResult Submit()
    {
        var timedOut = IsTimedOut;
        var elapsed = Elapsed;
        var results = Items
            .Select(i =>
            {
                var selected = _answers.TryGetValue(i.Id, out var idx) ? idx : (int?)null;
                return new ExamItemResult(i, selected, selected == i.CorrectIndex);
            })
            .ToList();

        IsSubmitted = true;
        return new MockExamResult(results, results.Count(r => r.Correct), results.Count, elapsed, timedOut);
    }

    /// Pulls each node's practice items from the content source and flattens them into one exam.
    public static MockExamSession ForNodes(
        IContentSource content, IEnumerable<string> nodeIds, TimeSpan duration, Func<DateTimeOffset>? nowProvider = null)
    {
        var items = nodeIds.SelectMany(content.GetPracticeItems).ToList();
        return new MockExamSession(items, duration, nowProvider);
    }
}
