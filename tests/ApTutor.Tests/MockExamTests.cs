using ApTutor.Curriculum;
using ApTutor.Platform;
using Xunit;

namespace ApTutor.Tests;

// Phase 6 acceptance: "a full timed mock runs; weak nodes reappear in MasteryTracker.Available()."
public class MockExamTests
{
    private static PracticeItem Item(string id, string nodeId, int correctIndex) =>
        new(id, nodeId, $"prompt for {id}", new[] { "a", "b", "c", "d" }, correctIndex, $"explanation for {id}");

    private sealed class FakeContentSource : IContentSource
    {
        private readonly IReadOnlyList<PracticeItem> _items;
        public FakeContentSource(IReadOnlyList<PracticeItem> items) => _items = items;
        public string GetWalkthroughText(string nodeId, string exampleId) => throw new NotImplementedException();
        public IReadOnlyList<PracticeItem> GetPracticeItems(string nodeId) =>
            _items.Where(i => i.NodeId == nodeId).ToList();
    }

    [Fact]
    public void Submit_AllCorrect_ScoresFullAndFlagsNoWeakNodes()
    {
        var items = new[] { Item("q1", "n1", 0), Item("q2", "n2", 1) };
        var session = new MockExamSession(items, TimeSpan.FromMinutes(10));

        session.Answer("q1", 0);
        session.Answer("q2", 1);
        var result = session.Submit();

        Assert.Equal(2, result.CorrectCount);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1.0, result.PercentCorrect);
        Assert.False(result.TimedOut);
        Assert.Empty(result.WeakNodeIds);
    }

    [Fact]
    public void Submit_WrongAnswer_FlagsThatItemsNodeAsWeak_ButNotOthers()
    {
        var items = new[] { Item("q1", "n1", 0), Item("q2", "n2", 1) };
        var session = new MockExamSession(items, TimeSpan.FromMinutes(10));

        session.Answer("q1", 2); // wrong
        session.Answer("q2", 1); // right
        var result = session.Submit();

        Assert.Equal(1, result.CorrectCount);
        Assert.Equal(new[] { "n1" }, result.WeakNodeIds);

        var q1 = result.Items.Single(i => i.Item.Id == "q1");
        Assert.False(q1.Correct);
        Assert.Equal(2, q1.SelectedIndex);
    }

    [Fact]
    public void Submit_UnansweredItem_CountsAsIncorrectNotAsError()
    {
        var items = new[] { Item("q1", "n1", 0) };
        var session = new MockExamSession(items, TimeSpan.FromMinutes(10));

        var result = session.Submit(); // never answered q1

        var q1 = Assert.Single(result.Items);
        Assert.Null(q1.SelectedIndex);
        Assert.False(q1.Correct);
        Assert.Equal(new[] { "n1" }, result.WeakNodeIds);
    }

    [Fact]
    public void ApplyWeakNodes_UnmastersFlaggedNodes_SoTheyReappearInAvailable()
    {
        var graph = new SkillGraph(TestDags.Diamond());
        var tracker = new MasteryTracker(graph);
        tracker.MarkMastered("A");
        tracker.MarkMastered("B");
        tracker.MarkMastered("C");

        var items = new[] { Item("q1", "A", 0) };
        var session = new MockExamSession(items, TimeSpan.FromMinutes(10));
        session.Answer("q1", 3); // wrong
        var result = session.Submit();

        result.ApplyWeakNodes(tracker);

        Assert.False(tracker.IsMastered("A"));
        Assert.Contains("A", tracker.Available().Select(n => n.Id));
    }

    [Fact]
    public void Answer_AfterTimeExpires_Throws()
    {
        var items = new[] { Item("q1", "n1", 0) };
        var now = DateTimeOffset.UtcNow;
        var session = new MockExamSession(items, TimeSpan.FromMinutes(1), () => now);

        now += TimeSpan.FromMinutes(2); // fast-forward past the deadline via the injected clock

        Assert.True(session.IsTimedOut);
        Assert.Throws<InvalidOperationException>(() => session.Answer("q1", 0));
    }

    [Fact]
    public void Submit_AfterTimeExpires_StillGradesWhateverWasAnswered()
    {
        var items = new[] { Item("q1", "n1", 0), Item("q2", "n2", 0) };
        var now = DateTimeOffset.UtcNow;
        var session = new MockExamSession(items, TimeSpan.FromMinutes(1), () => now);

        session.Answer("q1", 0); // answered before time runs out
        now += TimeSpan.FromMinutes(2);
        var result = session.Submit(); // q2 was never answered — clock ran out first

        Assert.True(result.TimedOut);
        Assert.Equal(1, result.CorrectCount);
        Assert.Null(result.Items.Single(i => i.Item.Id == "q2").SelectedIndex);
    }

    [Fact]
    public void Answer_UnknownItemId_Throws()
    {
        var session = new MockExamSession(new[] { Item("q1", "n1", 0) }, TimeSpan.FromMinutes(10));
        Assert.Throws<KeyNotFoundException>(() => session.Answer("ghost", 0));
    }

    [Fact]
    public void Constructor_EmptyItemList_Throws() =>
        Assert.Throws<ArgumentException>(() => new MockExamSession(Array.Empty<PracticeItem>(), TimeSpan.FromMinutes(10)));

    [Fact]
    public void ForNodes_FlattensItemsAcrossMultipleNodesFromContentSource()
    {
        var content = new FakeContentSource(new[] { Item("q1", "n1", 0), Item("q2", "n1", 1), Item("q3", "n2", 0) });

        var session = MockExamSession.ForNodes(content, new[] { "n1", "n2" }, TimeSpan.FromMinutes(10));

        Assert.Equal(3, session.Items.Count);
        Assert.Equal(new[] { "q1", "q2", "q3" }, session.Items.Select(i => i.Id));
    }
}
