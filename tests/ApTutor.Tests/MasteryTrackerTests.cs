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
}
