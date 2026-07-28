using ApTutor.Curriculum;
using Xunit;

namespace ApTutor.Tests;

public class MasteryTrackerTests
{
    // CLAUDE-HANDOFF §7: "With nothing mastered, Available() returns exactly the roots
    // (nodes with empty prereqs)."
    [Fact]
    public void Available_NothingMastered_ReturnsExactlyRoots()
    {
        var graph = new SkillGraph(TestDags.Diamond());
        var tracker = new MasteryTracker(graph);

        var available = tracker.Available().Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "A" }, available);
    }

    // CLAUDE-HANDOFF §7: "Mastering all of a node's prereqs makes it appear in Available()
    // on the next call."
    [Fact]
    public void Available_AfterMasteringPrereqs_UnlocksDependent()
    {
        var graph = new SkillGraph(TestDags.Diamond());
        var tracker = new MasteryTracker(graph);

        tracker.MarkMastered("A");
        var afterA = tracker.Available().Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "B", "C" }, afterA);
        Assert.DoesNotContain("D", afterA); // D needs both B and C, neither mastered yet

        tracker.MarkMastered("B");
        tracker.MarkMastered("C");
        var afterBC = tracker.Available().Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "D" }, afterBC);
    }

    // Phase 6 acceptance: "weak nodes reappear in MasteryTracker.Available()" — a missed mock-exam
    // question un-masters that node without touching anything else already mastered.
    [Fact]
    public void MarkWeak_UnmastersNode_SoItReappearsInAvailable()
    {
        var graph = new SkillGraph(TestDags.Diamond());
        var tracker = new MasteryTracker(graph);

        tracker.MarkMastered("A");
        tracker.MarkMastered("B");
        tracker.MarkMastered("C");
        Assert.DoesNotContain("A", tracker.Available().Select(n => n.Id));

        tracker.MarkWeak("A");

        Assert.False(tracker.IsMastered("A"));
        Assert.Contains("A", tracker.Available().Select(n => n.Id));
        // B and C were mastered independently of A being re-flagged weak — not cascaded.
        Assert.True(tracker.IsMastered("B"));
        Assert.True(tracker.IsMastered("C"));
    }

    [Fact]
    public void MarkWeak_UnknownNode_Throws() =>
        Assert.Throws<KeyNotFoundException>(() => new MasteryTracker(new SkillGraph(TestDags.Diamond())).MarkWeak("ghost"));
}
