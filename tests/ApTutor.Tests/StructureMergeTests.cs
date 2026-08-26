using ApTutor.ContentAdmin.Services;
using ApTutor.Curriculum;
using Xunit;

namespace ApTutor.Tests;

public class StructureMergeTests
{
    private static readonly UnitInfo Unit1 = new(1, "Unit One");
    private static readonly DagNode Unit1NodeA = new("u1.1", 1, NodeType.Concept, "First", Array.Empty<string>(), "");
    private static readonly DagNode Unit1NodeB = new("u1.2", 1, NodeType.Skill, "Second", new[] { "u1.1" }, "");

    private static SkillDag SampleDag() => new(
        Meta: new DagMeta("Test Course", 2),
        ScenePrimitives: new Dictionary<string, string>(),
        Units: new[] { Unit1 },
        Nodes: new[] { Unit1NodeA, Unit1NodeB });

    [Fact]
    public void UpsertUnit_OnEmptyStructure_AddsTheUnitAndItsNodes()
    {
        var empty = StructureMerge.EmptyStructure("worldhistory", "World History");
        var newNodes = new[] { new DagNode("u2.1", 2, NodeType.Concept, "Trans-Saharan trade routes", Array.Empty<string>(), "") };

        var updated = StructureMerge.UpsertUnit(empty, 2, "Networks of Exchange", newNodes);

        Assert.Single(updated.Units);
        Assert.Equal("Networks of Exchange", updated.Units[0].Title);
        Assert.Single(updated.Nodes);
        Assert.Equal("u2.1", updated.Nodes[0].Id);
        Assert.Equal(1, updated.Meta.NodeCount);
    }

    [Fact]
    public void UpsertUnit_NewUnitOnExistingStructure_AddsIt_LeavesExistingUnitUntouched()
    {
        var current = SampleDag();
        var newNodes = new[] { new DagNode("u2.1", 2, NodeType.Concept, "New unit node", Array.Empty<string>(), "") };

        var updated = StructureMerge.UpsertUnit(current, 2, "Unit Two", newNodes);

        Assert.Equal(2, updated.Units.Count);
        Assert.Contains(updated.Units, u => u.Unit == 1 && u.Title == "Unit One");
        Assert.Contains(updated.Units, u => u.Unit == 2 && u.Title == "Unit Two");

        Assert.Equal(3, updated.Nodes.Count);
        Assert.Contains(updated.Nodes, n => n.Id == "u1.1");
        Assert.Contains(updated.Nodes, n => n.Id == "u1.2");
        Assert.Contains(updated.Nodes, n => n.Id == "u2.1");
        Assert.Equal(3, updated.Meta.NodeCount);
    }

    [Fact]
    public void UpsertUnit_RegeneratingAnExistingUnit_ReplacesItsNodes_LeavesOtherUnitsAlone()
    {
        var current = StructureMerge.UpsertUnit(
            SampleDag(), 2, "Unit Two",
            new[] { new DagNode("u2.1", 2, NodeType.Concept, "Old u2.1", Array.Empty<string>(), "") });

        var regenerated = new[] { new DagNode("u2.1", 2, NodeType.Concept, "New u2.1", Array.Empty<string>(), "") };
        var updated = StructureMerge.UpsertUnit(current, 2, "Unit Two Retitled", regenerated);

        Assert.Equal(2, updated.Units.Count);
        Assert.Contains(updated.Units, u => u.Unit == 2 && u.Title == "Unit Two Retitled");

        Assert.Equal(3, updated.Nodes.Count); // unit 1's two nodes + unit 2's one (replaced, not appended)
        Assert.Contains(updated.Nodes, n => n.Id == "u1.1");
        Assert.Contains(updated.Nodes, n => n.Id == "u1.2");
        var unit2Node = Assert.Single(updated.Nodes, n => n.Id == "u2.1");
        Assert.Equal("New u2.1", unit2Node.Title);
    }

    [Fact]
    public void UpsertUnit_DoesNotMutateTheOriginalStructure()
    {
        var original = SampleDag();
        StructureMerge.UpsertUnit(original, 2, "Unit Two", new[] { new DagNode("u2.1", 2, NodeType.Concept, "New", Array.Empty<string>(), "") });

        Assert.Single(original.Units);
        Assert.Equal(2, original.Nodes.Count);
    }

    [Fact]
    public void EmptyStructure_HasNoUnitsOrNodes()
    {
        var empty = StructureMerge.EmptyStructure("newcourse", "AP Physics 1");

        Assert.Empty(empty.Units);
        Assert.Empty(empty.Nodes);
        Assert.Equal(0, empty.Meta.NodeCount);
        Assert.Equal("AP Physics 1", empty.Meta.DisplayName);
    }
}
