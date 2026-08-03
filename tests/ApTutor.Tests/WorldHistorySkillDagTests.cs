using ApTutor.Curriculum;
using Xunit;

namespace ApTutor.Tests;

// World History pilot (BUILD-PLAN.md "Course model"): the second course DAG, proving
// SkillDagLoader/SkillGraph are genuinely course-agnostic and not CS-A-specific. Mirrors
// SkillDagLoaderTests' shape for the shipped CS A DAG.
public class WorldHistorySkillDagTests
{
    private static SkillGraph LoadShipped() =>
        SkillDagLoader.Load(Path.Combine(AppContext.BaseDirectory, "apwh-skill-dag.json"));

    [Fact]
    public void Load_ShippedJson_DoesNotThrow_NodeCountMatchesMeta()
    {
        // SkillGraph's constructor validates the DAG (no duplicate ids, no unknown prereqs, no
        // cycle — see SkillGraphValidationTests) and throws InvalidDataException if it isn't
        // well-formed, so simply loading without throwing is itself the acyclic-DAG assertion.
        var graph = LoadShipped();

        Assert.Equal(graph.Dag.Meta.NodeCount, graph.Dag.Nodes.Count);
    }

    [Fact]
    public void Load_ShippedJson_HasAtLeastOneRootNode()
    {
        var graph = LoadShipped();

        Assert.Contains(graph.Dag.Nodes, n => n.Prereqs.Count == 0);
    }

    [Fact]
    public void Load_ShippedJson_EveryNodeVizIsNonePlaceholder()
    {
        // No history-appropriate scene primitives (timeline/map/cause-effect) exist yet — see
        // apwh-skill-dag.json's meta.purpose and WorldHistoryCourseModule's doc comment — so every
        // node uses the "none" placeholder rather than a fake primitive name nothing renders.
        var graph = LoadShipped();

        Assert.All(graph.Dag.Nodes, n => Assert.Equal("none", n.Viz));
    }
}
