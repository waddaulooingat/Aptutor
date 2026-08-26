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

    // Load and LoadFromJson must validate identically — Load is just File.ReadAllText piped into
    // LoadFromJson, not a separate code path (see the Shell-display-only/course-authoring plan,
    // which needs LoadFromJson directly for DAG bytes downloaded from S3, never touching disk).
    [Fact]
    public void LoadFromJson_ShippedJsonReadManually_MatchesLoad()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "apcsa-skill-dag.json");
        var json = File.ReadAllText(path);

        var graph = SkillDagLoader.LoadFromJson(json);

        Assert.Equal(graph.Dag.Meta.NodeCount, graph.Dag.Nodes.Count);
    }

    [Fact]
    public void LoadFromJson_MalformedJson_Throws() =>
        Assert.ThrowsAny<Exception>(() => SkillDagLoader.LoadFromJson("{ not valid json"));

    [Fact]
    public void LoadFromJson_DuplicateNodeId_Throws()
    {
        const string json = """
            {
              "meta": { "course": "Test", "nodeCount": 2 },
              "scenePrimitives": {},
              "units": [ { "unit": 1, "title": "Unit 1" } ],
              "nodes": [
                { "id": "u1.1", "unit": 1, "type": "concept", "title": "A", "prereqs": [], "viz": "" },
                { "id": "u1.1", "unit": 1, "type": "concept", "title": "B", "prereqs": [], "viz": "" }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(() => SkillDagLoader.LoadFromJson(json));
    }
}
