using System.Net;
using System.Text;
using ApTutor.Content;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;
using ApTutor.Platform;
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

        var pack = await generator.GenerateAsync("csa", SampleNode, Difficulty.Hard, allowGraphChoices: false);

        Assert.Equal("csa", pack.CourseId);
        Assert.Equal("u1.2", pack.NodeId);
        Assert.False(pack.Verified); // always starts unverified — the reviewer flips this
        Assert.Equal("fake-model", pack.Model);
        Assert.Equal("Variables hold typed values.", pack.WalkthroughText);
        Assert.Equal(Difficulty.Hard, pack.Difficulty); // assigned from the caller, not parsed from the model

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
        await Assert.ThrowsAnyAsync<Exception>(() => generator.GenerateAsync("csa", SampleNode, Difficulty.Medium, allowGraphChoices: false));
    }

    // GenerateAsync with allowGraphChoices: true (see the graph-spec-rendering plan's Part 2) — a
    // practice item's choices can now be graphs, text, or a mix, discriminated by "kind".
    private static readonly DagNode PhysicsNode = new(
        "u1.1", 1, NodeType.Concept, "Velocity vs. time graphs", Array.Empty<string>(), "graph");

    private const string GraphChoiceResponse = """
        {
            "content": [
                {
                    "type": "tool_use",
                    "name": "emit_node_content",
                    "input": {
                        "walkthroughText": "A velocity-time graph's slope is acceleration.",
                        "practiceItems": [
                            {
                                "prompt": "Which graph shows an object at constant velocity?",
                                "choices": [
                                    {
                                        "kind": "graph",
                                        "graph": {
                                            "xAxis": { "label": "Time", "unit": "s" },
                                            "yAxis": { "label": "Velocity", "unit": "m/s", "min": 0, "max": 10 },
                                            "segments": [
                                                { "points": [ { "x": 0, "y": 5 }, { "x": 10, "y": 5 } ], "solid": true }
                                            ],
                                            "referenceValues": [ { "axis": "y", "value": 5, "label": "v_t" } ]
                                        }
                                    },
                                    {
                                        "kind": "graph",
                                        "graph": {
                                            "xAxis": { "label": "Time", "unit": "s" },
                                            "yAxis": { "label": "Velocity", "unit": "m/s" },
                                            "segments": [
                                                { "points": [ { "x": 0, "y": 0 }, { "x": 10, "y": 10 } ], "solid": true }
                                            ]
                                        }
                                    },
                                    { "kind": "text", "text": "Neither graph shows constant velocity" },
                                    { "kind": "text", "text": "Both graphs show constant velocity" }
                                ],
                                "correctIndex": 0,
                                "explanation": "A flat line on a velocity-time graph means velocity isn't changing."
                            }
                        ],
                        "walkthroughSteps": [
                            { "caption": "Plot velocity over time.", "sourceLine": null, "ops": [ { "op": "lineHighlight", "line": 1 } ] }
                        ]
                    }
                }
            ]
        }
        """;

    [Fact]
    public async Task GenerateAsync_AllowGraphChoicesTrue_MixedTextAndGraphChoices_ParsesCorrectly()
    {
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(GraphChoiceResponse));
        var generator = new Generator(client);

        var pack = await generator.GenerateAsync("physics1", PhysicsNode, Difficulty.Medium, allowGraphChoices: true);

        var item = Assert.Single(pack.PracticeItems);
        Assert.Equal(4, item.Choices.Count);

        Assert.True(item.Choices[0].IsGraph);
        Assert.Equal("Time", item.Choices[0].Graph!.XAxis.Label);
        Assert.Equal("s", item.Choices[0].Graph!.XAxis.Unit);
        Assert.Equal("Velocity", item.Choices[0].Graph!.YAxis.Label);
        Assert.Equal(0, item.Choices[0].Graph!.YAxis.Min);
        Assert.Equal(10, item.Choices[0].Graph!.YAxis.Max);
        Assert.Single(item.Choices[0].Graph!.Segments);
        Assert.True(item.Choices[0].Graph!.Segments[0].Solid);
        Assert.Equal(2, item.Choices[0].Graph!.Segments[0].Points.Count);
        Assert.NotNull(item.Choices[0].Graph!.ReferenceValues);
        Assert.Equal(GraphAxisKind.Y, item.Choices[0].Graph!.ReferenceValues![0].Axis);
        Assert.Equal("v_t", item.Choices[0].Graph!.ReferenceValues![0].Label);

        Assert.True(item.Choices[1].IsGraph);
        Assert.Null(item.Choices[1].Graph!.ReferenceValues); // optional field genuinely omitted

        Assert.False(item.Choices[2].IsGraph);
        Assert.Equal("Neither graph shows constant velocity", item.Choices[2].Text);
        Assert.False(item.Choices[3].IsGraph);
    }

    [Fact]
    public async Task GenerateAsync_AllowGraphChoicesFalse_StillAcceptsPlainStringChoices()
    {
        // The unchanged, pre-graph-spec wire shape — GeneratedChoiceConverter must keep parsing it
        // exactly as before when the caller hasn't opted into graph-based choices.
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(CannedResponse));
        var generator = new Generator(client);

        var pack = await generator.GenerateAsync("csa", SampleNode, Difficulty.Medium, allowGraphChoices: false);

        var item = Assert.Single(pack.PracticeItems);
        Assert.All(item.Choices, c => Assert.False(c.IsGraph));
    }

    [Fact]
    public async Task GenerateAsync_GraphChoice_SegmentWithOnlyOnePoint_ThrowsRatherThanShippingAnUndrawableLine()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_node_content", "input": {
                "walkthroughText": "text",
                "practiceItems": [ { "prompt": "p", "correctIndex": 0, "explanation": "e", "choices": [
                    { "kind": "graph", "graph": {
                        "xAxis": { "label": "Time" }, "yAxis": { "label": "Velocity" },
                        "segments": [ { "points": [ { "x": 0, "y": 0 } ] } ] } },
                    { "kind": "text", "text": "b" }, { "kind": "text", "text": "c" }, { "kind": "text", "text": "d" }
                ] } ],
                "walkthroughSteps": [ { "caption": "c", "ops": [] } ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync("physics1", PhysicsNode, Difficulty.Medium, allowGraphChoices: true));
    }

    [Fact]
    public async Task GenerateAsync_UnrecognizedChoiceKind_Throws()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_node_content", "input": {
                "walkthroughText": "text",
                "practiceItems": [ { "prompt": "p", "correctIndex": 0, "explanation": "e", "choices": [
                    { "kind": "image" }, { "kind": "text", "text": "b" }, { "kind": "text", "text": "c" }, { "kind": "text", "text": "d" }
                ] } ],
                "walkthroughSteps": [ { "caption": "c", "ops": [] } ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync("physics1", PhysicsNode, Difficulty.Medium, allowGraphChoices: true));
    }

    [Fact]
    public async Task GenerateAsync_GraphChoiceMissingXAxisLabel_Throws()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_node_content", "input": {
                "walkthroughText": "text",
                "practiceItems": [ { "prompt": "p", "correctIndex": 0, "explanation": "e", "choices": [
                    { "kind": "graph", "graph": {
                        "xAxis": { "label": "" }, "yAxis": { "label": "Velocity" },
                        "segments": [ { "points": [ { "x": 0, "y": 0 }, { "x": 1, "y": 1 } ] } ] } },
                    { "kind": "text", "text": "b" }, { "kind": "text", "text": "c" }, { "kind": "text", "text": "d" }
                ] } ],
                "walkthroughSteps": [ { "caption": "c", "ops": [] } ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync("physics1", PhysicsNode, Difficulty.Medium, allowGraphChoices: true));
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

    // GenerateLearnContentAsync (see the learn-quiz-mode-switch plan's Part B) — teaching content
    // for a node, generated and reviewed alongside (not replacing) practice items.
    private const string LearnContentResponse = """
        {
            "content": [
                {
                    "type": "tool_use",
                    "name": "emit_learn_content",
                    "input": {
                        "overview": "This node covers variables and primitive types.",
                        "steps": [
                            { "caption": "Declare a variable.", "detail": "int x = 5; reserves memory typed as int." },
                            { "caption": "Reassign it." }
                        ]
                    }
                }
            ]
        }
        """;

    [Fact]
    public async Task GenerateLearnContentAsync_ParsesCannedResponseIntoLearnContent()
    {
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(LearnContentResponse));
        var generator = new Generator(client);

        var content = await generator.GenerateLearnContentAsync("csa", SampleNode);

        Assert.Equal("This node covers variables and primitive types.", content.Overview);
        Assert.False(content.Verified); // always starts unverified — the reviewer flips this
        Assert.Equal("fake-model", content.Model);
        Assert.Equal(2, content.Steps.Count);
        Assert.Equal("Declare a variable.", content.Steps[0].Caption);
        Assert.Equal("int x = 5; reserves memory typed as int.", content.Steps[0].Detail);
        Assert.Null(content.Steps[1].Detail); // detail is optional per step
    }

    [Fact]
    public async Task GenerateLearnContentAsync_MissingOverview_Throws()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_learn_content", "input": {
                "overview": "", "steps": [ { "caption": "A step." } ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateLearnContentAsync("csa", SampleNode));
    }

    [Fact]
    public async Task GenerateLearnContentAsync_ZeroSteps_Throws()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_learn_content", "input": {
                "overview": "An overview.", "steps": [] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateLearnContentAsync("csa", SampleNode));
    }

    [Fact]
    public async Task GenerateLearnContentAsync_StepMissingCaption_Throws()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_learn_content", "input": {
                "overview": "An overview.", "steps": [ { "caption": "" } ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateLearnContentAsync("csa", SampleNode));
    }

    // Production hit this: the model sometimes drops the {caption,detail} object wrapper and emits
    // steps as bare strings instead — GeneratedLearnStepConverter should accept that shape too.
    [Fact]
    public async Task GenerateLearnContentAsync_StepsAsBareStrings_ParsesWithNoDetail()
    {
        const string response = """
            { "content": [ { "type": "tool_use", "name": "emit_learn_content", "input": {
                "overview": "An overview.",
                "steps": [ "Declare a variable.", "Reassign it." ] } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(response));
        var generator = new Generator(client);

        var content = await generator.GenerateLearnContentAsync("csa", SampleNode);

        Assert.Equal(2, content.Steps.Count);
        Assert.Equal("Declare a variable.", content.Steps[0].Caption);
        Assert.Null(content.Steps[0].Detail);
    }

    // Production also hit this: for a single-step response the model sometimes drops the array
    // wrapper entirely and returns "steps" as a bare object instead of a one-element array.
    [Fact]
    public async Task GenerateLearnContentAsync_StepsAsBareObject_WrapsInSingleElementList()
    {
        const string response = """
            { "content": [ { "type": "tool_use", "name": "emit_learn_content", "input": {
                "overview": "An overview.",
                "steps": { "caption": "The only step.", "detail": "Some detail." } } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(response));
        var generator = new Generator(client);

        var content = await generator.GenerateLearnContentAsync("csa", SampleNode);

        var step = Assert.Single(content.Steps);
        Assert.Equal("The only step.", step.Caption);
        Assert.Equal("Some detail.", step.Detail);
    }
}
