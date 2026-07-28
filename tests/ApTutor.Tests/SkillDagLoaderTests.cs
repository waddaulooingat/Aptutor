using ApTutor.Curriculum;
using Xunit;

namespace ApTutor.Tests;

public class SkillDagLoaderTests
{
    // CLAUDE-HANDOFF §7: "Loads the shipped JSON; Nodes.Count == meta.nodeCount."
    [Fact]
    public void Load_ShippedJson_NodeCountMatchesMeta()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "apcsa-skill-dag.json");

        var graph = SkillDagLoader.Load(path);

        Assert.Equal(graph.Dag.Meta.NodeCount, graph.Dag.Nodes.Count);
    }
}
