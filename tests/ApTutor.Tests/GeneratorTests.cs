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

    // Dev-only "Refresh questions" (MainWindow's right-click): a smaller, faster call that only
    // regenerates practice items, no walkthrough — same parsing discipline as the full generator.
    private const string PracticeItemsOnlyResponse = """
        {
            "content": [
                {
                    "type": "tool_use",
                    "name": "emit_practice_items",
                    "input": {
                        "practiceItems": [
                            { "prompt": "Refreshed Q1?", "choices": ["a", "b", "c", "d"], "correctIndex": 0, "explanation": "e1" },
                            { "prompt": "Refreshed Q2?", "choices": ["a", "b", "c", "d"], "correctIndex": 3, "explanation": "e2" }
                        ]
                    }
                }
            ]
        }
        """;

    [Fact]
    public async Task RegeneratePracticeItemsAsync_ParsesCannedResponseIntoPracticeItems()
    {
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(PracticeItemsOnlyResponse));
        var generator = new Generator(client);

        var items = await generator.RegeneratePracticeItemsAsync("csa", SampleNode);

        Assert.Equal(2, items.Count);
        Assert.Equal("u1.2-q1", items[0].Id); // assigned by us, not the model
        Assert.Equal("u1.2", items[0].NodeId);
        Assert.Equal("Refreshed Q1?", items[0].Prompt);
        Assert.Equal("u1.2-q2", items[1].Id);
        Assert.Equal(3, items[1].CorrectIndex);
    }

    [Fact]
    public async Task RegeneratePracticeItemsAsync_MalformedToolInput_Throws()
    {
        const string badResponse = """
            { "content": [ { "type": "tool_use", "name": "emit_practice_items", "input": { "somethingElse": true } } ] }
            """;
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(badResponse));
        var generator = new Generator(client);

        await Assert.ThrowsAnyAsync<Exception>(() => generator.RegeneratePracticeItemsAsync("csa", SampleNode));
    }
}
