using System.Net;
using System.Text;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;
using Xunit;

namespace ApTutor.Tests;

// Phase 7: Generator.GenerateAsync against a fake Claude response — no network, no API key.
// Exercises the full parse path: tool_use input -> GeneratedNodeContent -> real NodeContentPack,
// including assigning structural fields (practice item ids, step indices) ourselves rather than
// trusting the model to invent them.
public class GeneratorTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        public FakeHandler(string responseJson) => _responseJson = responseJson;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            });
    }

    private static readonly DagNode SampleNode = new(
        "u1.2", 1, NodeType.Skill, "Variables and primitive data types", new[] { "u1.1" }, "memCell");

    private const string CannedResponse = """
        {
            "content": [
                {
                    "type": "tool_use",
                    "name": "emit_node_content",
                    "input": {
                        "walkthroughText": "Variables hold typed values.",
                        "practiceItems": [
                            { "prompt": "What is int x = 5;?", "choices": ["a", "b", "c", "d"], "correctIndex": 2, "explanation": "because" }
                        ],
                        "walkthroughSteps": [
                            {
                                "caption": "Declare x.",
                                "sourceLine": 1,
                                "ops": [ { "op": "framePush", "frameId": "main", "methodSig": "void main()" } ]
                            },
                            {
                                "caption": "Set x to 5.",
                                "sourceLine": 2,
                                "ops": [ { "op": "memCellSet", "frameId": "main", "name": "x", "type": "int", "value": "5" } ]
                            }
                        ]
                    }
                }
            ]
        }
        """;

    [Fact]
    public async Task GenerateAsync_ParsesCannedResponseIntoNodeContentPack()
    {
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(CannedResponse));
        var generator = new Generator(client);

        var pack = await generator.GenerateAsync("csa", SampleNode);

        Assert.Equal("csa", pack.CourseId);
        Assert.Equal("u1.2", pack.NodeId);
        Assert.False(pack.Verified); // always starts unverified — the reviewer flips this
        Assert.Equal("fake-model", pack.Model);
        Assert.Equal("Variables hold typed values.", pack.WalkthroughText);

        var item = Assert.Single(pack.PracticeItems);
        Assert.Equal("u1.2-q1", item.Id); // assigned by us, not the model
        Assert.Equal("u1.2", item.NodeId);
        Assert.Equal(2, item.CorrectIndex);

        Assert.Equal(2, pack.WalkthroughSteps.Count);
        Assert.Equal(0, pack.WalkthroughSteps[0].Index); // assigned by us
        Assert.Equal(1, pack.WalkthroughSteps[1].Index);
        Assert.Equal("Declare x.", pack.WalkthroughSteps[0].Caption);

        var op = Assert.IsType<ApTutor.Scene.FramePush>(pack.WalkthroughSteps[0].Delta.Ops[0]);
        Assert.Equal("main", op.FrameId);
    }

    [Fact]
    public async Task GenerateAsync_MalformedToolInput_ThrowsRatherThanShippingGarbage()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_node_content", "input": { "walkthroughText": "only this field" } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        // Missing required practiceItems/walkthroughSteps -> deserialization leaves them null ->
        // the .Select(...) call on a null list throws, which is the desired "fail loudly, don't
        // silently ship half-generated content" behavior.
        await Assert.ThrowsAnyAsync<Exception>(() => generator.GenerateAsync("csa", SampleNode));
    }

    // GenerateUnitStructureAsync (see the Shell-display-only/course-authoring plan's Part B) —
    // drafts a unit's node list rather than any one node's content.
    private const string UnitStructureResponse = """
        {
            "content": [
                {
                    "type": "tool_use",
                    "name": "emit_unit_structure",
                    "input": {
                        "nodes": [
                            { "id": "u2.1", "title": "Trans-Saharan trade routes", "type": "concept", "prereqs": [], "viz": "" },
                            { "id": "u2.2", "title": "Indian Ocean trade network", "type": "concept", "prereqs": ["u2.1"], "viz": "" }
                        ]
                    }
                }
            ]
        }
        """;

    [Fact]
    public async Task GenerateUnitStructureAsync_ParsesCannedResponseIntoDagNodes()
    {
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(UnitStructureResponse));
        var generator = new Generator(client);

        var nodes = await generator.GenerateUnitStructureAsync("worldhistory", 2, "Networks of Exchange", Array.Empty<DagNode>(), guidance: null);

        Assert.Equal(2, nodes.Count);
        Assert.Equal("u2.1", nodes[0].Id);
        Assert.Equal(2, nodes[0].Unit); // assigned by us from unitNumber, not parsed from the model
        Assert.Equal(NodeType.Concept, nodes[0].Type);
        Assert.Empty(nodes[0].Prereqs);
        Assert.Equal("u2.2", nodes[1].Id);
        Assert.Equal(new[] { "u2.1" }, nodes[1].Prereqs);
    }

    [Fact]
    public async Task GenerateUnitStructureAsync_NodeIdMissingUnitPrefix_Throws()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_unit_structure", "input": {
                "nodes": [ { "id": "u3.1", "title": "Wrong unit", "type": "concept", "prereqs": [], "viz": "" } ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateUnitStructureAsync("worldhistory", 2, "Networks of Exchange", Array.Empty<DagNode>(), guidance: null));
    }

    [Fact]
    public async Task GenerateUnitStructureAsync_DuplicateNodeId_Throws()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_unit_structure", "input": {
                "nodes": [
                    { "id": "u2.1", "title": "First", "type": "concept", "prereqs": [], "viz": "" },
                    { "id": "u2.1", "title": "Duplicate", "type": "concept", "prereqs": [], "viz": "" }
                ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateUnitStructureAsync("worldhistory", 2, "Networks of Exchange", Array.Empty<DagNode>(), guidance: null));
    }

    [Fact]
    public async Task GenerateUnitStructureAsync_UnrecognizedNodeType_Throws()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_unit_structure", "input": {
                "nodes": [ { "id": "u2.1", "title": "Bad type", "type": "not-a-real-type", "prereqs": [], "viz": "" } ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateUnitStructureAsync("worldhistory", 2, "Networks of Exchange", Array.Empty<DagNode>(), guidance: null));
    }

    [Fact]
    public async Task GenerateUnitStructureAsync_ZeroNodes_Throws()
    {
        const string emptyResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_unit_structure", "input": { "nodes": [] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(emptyResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateUnitStructureAsync("worldhistory", 2, "Networks of Exchange", Array.Empty<DagNode>(), guidance: null));
    }

    // GenerateCourseUnitListAsync (see the Shell-display-only/course-authoring plan's Part C) —
    // drafts only a course's unit list (table of contents), never any node structure.
    private const string CourseUnitListResponse = """
        {
            "content": [
                {
                    "type": "tool_use",
                    "name": "emit_course_units",
                    "input": {
                        "units": [
                            { "unit": 1, "title": "Kinematics" },
                            { "unit": 2, "title": "Dynamics" }
                        ]
                    }
                }
            ]
        }
        """;

    [Fact]
    public async Task GenerateCourseUnitListAsync_ParsesCannedResponseIntoUnitInfos()
    {
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(CourseUnitListResponse));
        var generator = new Generator(client);

        var units = await generator.GenerateCourseUnitListAsync("physics1", "AP Physics 1", guidance: null);

        Assert.Equal(2, units.Count);
        Assert.Equal(1, units[0].Unit);
        Assert.Equal("Kinematics", units[0].Title);
        Assert.Equal(2, units[1].Unit);
        Assert.Equal("Dynamics", units[1].Title);
    }

    [Fact]
    public async Task GenerateCourseUnitListAsync_OutOfOrderResponse_IsSortedByUnitNumber()
    {
        const string outOfOrderResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_course_units", "input": {
                "units": [ { "unit": 2, "title": "Second" }, { "unit": 1, "title": "First" } ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(outOfOrderResponse));
        var generator = new Generator(client);

        var units = await generator.GenerateCourseUnitListAsync("physics1", "AP Physics 1", guidance: null);

        Assert.Equal(new[] { 1, 2 }, units.Select(u => u.Unit));
    }

    [Fact]
    public async Task GenerateCourseUnitListAsync_DuplicateUnitNumber_Throws()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_course_units", "input": {
                "units": [ { "unit": 1, "title": "First" }, { "unit": 1, "title": "Duplicate" } ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateCourseUnitListAsync("physics1", "AP Physics 1", guidance: null));
    }

    [Fact]
    public async Task GenerateCourseUnitListAsync_ZeroUnits_Throws()
    {
        const string emptyResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_course_units", "input": { "units": [] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(emptyResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateCourseUnitListAsync("physics1", "AP Physics 1", guidance: null));
    }
}
