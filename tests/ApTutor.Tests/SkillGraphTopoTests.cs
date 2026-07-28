using ApTutor.Curriculum;
using Xunit;

namespace ApTutor.Tests;

// CLAUDE-HANDOFF §7: "TopologicalOrder: every node appears after all its prereqs."
public class SkillGraphTopoTests
{
    [Fact]
    public void TopologicalOrder_EveryNodeAfterItsPrereqs()
    {
        var graph = new SkillGraph(TestDags.Diamond());
        var position = graph.TopologicalOrder
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);

        foreach (var id in graph.TopologicalOrder)
            foreach (var prereq in graph.Node(id).Prereqs)
                Assert.True(position[prereq] < position[id],
                    $"'{prereq}' (prereq of '{id}') must come before it in topological order.");
    }

    [Fact]
    public void TopologicalOrder_ShippedCurriculum_EveryNodeAfterItsPrereqs()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "apcsa-skill-dag.json");
        var graph = SkillDagLoader.Load(path);

        var position = graph.TopologicalOrder
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);

        foreach (var id in graph.TopologicalOrder)
            foreach (var prereq in graph.Node(id).Prereqs)
                Assert.True(position[prereq] < position[id],
                    $"'{prereq}' (prereq of '{id}') must come before it in topological order.");
    }
}
