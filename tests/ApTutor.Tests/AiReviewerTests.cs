using System.Net;
using System.Text;
using ApTutor.Content;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;
using ApTutor.Platform;
using Xunit;

namespace ApTutor.Tests;

// AiReviewer is the "brain" of the AI Content Agent's interim review pass (see the handoff) — these
// tests exercise its verdict parsing against canned Claude tool_use responses, the same FakeHandler
// pattern GeneratorTests uses so nothing here makes a real network call.
public class AiReviewerTests
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

    private static NodeContentPack SamplePack() => new(
        CourseId: "csa", NodeId: "u1.2", ExampleId: "generated", WalkthroughText: "A variable stores a value.",
        PracticeItems: new[]
        {
            new PracticeItem("p1", "u1.2", "What does `int x = 5;` do?",
                new[] { PracticeItemChoice.OfText("Declares x as an int and sets it to 5"), PracticeItemChoice.OfText("Deletes x") },
                CorrectIndex: 0, Explanation: "It declares and initializes x."),
        },
        WalkthroughSteps: Array.Empty<VisualStep>(), Verified: false, GeneratedAt: DateTimeOffset.UtcNow, Model: "test-model");

    private static LearnContent SampleLearnContent() => new(
        "Variables store values.", new[] { new LearnStep("Declare a variable.", "int x = 5;") },
        Verified: false, GeneratedAt: DateTimeOffset.UtcNow, Model: "test-model");

    private static string CannedVerdict(string verdict, string reasoning) => $$"""
        { "content": [ { "type": "tool_use", "name": "emit_review_verdict", "input": {
            "verdict": "{{verdict}}", "reasoning": "{{reasoning}}" } } ] }
        """;

    [Fact]
    public async Task ReviewNodeContentAsync_ApproveVerdict_ReturnsApprove()
    {
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(CannedVerdict("approve", "Correct and clear.")));
        var reviewer = new AiReviewer(client);

        var result = await reviewer.ReviewNodeContentAsync(SampleNode, Difficulty.Medium, SamplePack());

        Assert.Equal(AiReviewVerdict.Approve, result.Verdict);
        Assert.Equal("Correct and clear.", result.Reasoning);
    }

    [Fact]
    public async Task ReviewNodeContentAsync_FlagVerdict_ReturnsFlagWithReasoning()
    {
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(CannedVerdict("flag", "The explanation has a factual error.")));
        var reviewer = new AiReviewer(client);

        var result = await reviewer.ReviewNodeContentAsync(SampleNode, Difficulty.Medium, SamplePack());

        Assert.Equal(AiReviewVerdict.Flag, result.Verdict);
        Assert.Equal("The explanation has a factual error.", result.Reasoning);
    }

    [Fact]
    public async Task ReviewNodeContentAsync_UnrecognizedVerdictString_ResolvesToFlag()
    {
        // "when in doubt, flag" applies to the reviewer's own output shape too — a malformed verdict
        // must never silently resolve to Approve (see AiReviewer.ParseVerdict's remarks).
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(CannedVerdict("maybe", "Not sure.")));
        var reviewer = new AiReviewer(client);

        var result = await reviewer.ReviewNodeContentAsync(SampleNode, Difficulty.Medium, SamplePack());

        Assert.Equal(AiReviewVerdict.Flag, result.Verdict);
    }

    [Fact]
    public async Task ReviewNodeContentAsync_VerdictIsCaseInsensitive()
    {
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(CannedVerdict("APPROVE", "Looks good.")));
        var reviewer = new AiReviewer(client);

        var result = await reviewer.ReviewNodeContentAsync(SampleNode, Difficulty.Medium, SamplePack());

        Assert.Equal(AiReviewVerdict.Approve, result.Verdict);
    }

    [Fact]
    public async Task ReviewLearnContentAsync_ApproveVerdict_ReturnsApprove()
    {
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(CannedVerdict("approve", "Clear and accurate.")));
        var reviewer = new AiReviewer(client);

        var result = await reviewer.ReviewLearnContentAsync(SampleNode, SampleLearnContent());

        Assert.Equal(AiReviewVerdict.Approve, result.Verdict);
    }

    [Fact]
    public async Task ReviewNodeContentAsync_GraphBasedChoice_DescribesItStructurallyWithoutThrowing()
    {
        // A graph-based choice (see the graph-spec-rendering plan) has no Text — DescribeChoice must
        // fall back to a structural description instead of crashing on a null Text.
        var pack = SamplePack() with
        {
            PracticeItems = new[]
            {
                new PracticeItem("p1", "u1.2", "Which graph shows constant velocity?",
                    new[]
                    {
                        PracticeItemChoice.OfGraph(new GraphSpec(
                            new GraphAxis("Time", "s"), new GraphAxis("Velocity", "m/s"),
                            new[] { new GraphSegment(new[] { new GraphPoint(0, 5), new GraphPoint(10, 5) }) })),
                        PracticeItemChoice.OfText("None of these"),
                    },
                    CorrectIndex: 0, Explanation: "A flat line means constant velocity."),
            },
        };
        var client = new ClaudeClient("fake-key", "fake-model", new FakeHandler(CannedVerdict("approve", "Fine.")));
        var reviewer = new AiReviewer(client);

        var result = await reviewer.ReviewNodeContentAsync(SampleNode, Difficulty.Medium, pack);

        Assert.Equal(AiReviewVerdict.Approve, result.Verdict);
    }
}
