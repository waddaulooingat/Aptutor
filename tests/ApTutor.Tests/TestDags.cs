using ApTutor.Curriculum;

namespace ApTutor.Tests;

/// Small hand-built DAGs for validation/topo/mastery tests — cheaper and more focused than
/// exercising the 69-node shipped curriculum for every case.
internal static class TestDags
{
    private static readonly IReadOnlyDictionary<string, string> NoPrimitives =
        new Dictionary<string, string>();

    private static readonly IReadOnlyList<UnitInfo> OneUnit = new[] { new UnitInfo(1, "Unit One") };

    private static DagNode Node(string id, string[] prereqs) =>
        new(id, 1, NodeType.Concept, id, prereqs, "lineHighlight");

    /// A -> B -> C : a straight chain, each node prereq'd on exactly the one before it.
    public static SkillDag Chain() => new(
        new DagMeta("Test", 3), NoPrimitives, OneUnit,
        new[]
        {
            Node("A", Array.Empty<string>()),
            Node("B", new[] { "A" }),
            Node("C", new[] { "B" }),
        });

    /// Diamond: A is the sole root; B and C both need A; D needs both B and C.
    public static SkillDag Diamond() => new(
        new DagMeta("Test", 4), NoPrimitives, OneUnit,
        new[]
        {
            Node("A", Array.Empty<string>()),
            Node("B", new[] { "A" }),
            Node("C", new[] { "A" }),
            Node("D", new[] { "B", "C" }),
        });

    public static SkillDag WithDuplicateId() => new(
        new DagMeta("Test", 2), NoPrimitives, OneUnit,
        new[]
        {
            Node("A", Array.Empty<string>()),
            Node("A", Array.Empty<string>()),
        });

    public static SkillDag WithUnknownPrereq() => new(
        new DagMeta("Test", 1), NoPrimitives, OneUnit,
        new[]
        {
            Node("A", new[] { "ghost" }),
        });

    public static SkillDag WithCycle() => new(
        new DagMeta("Test", 2), NoPrimitives, OneUnit,
        new[]
        {
            Node("A", new[] { "B" }),
            Node("B", new[] { "A" }),
        });
}
