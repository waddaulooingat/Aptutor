using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ApTutor.Client.Services;
using ApTutor.ContentFactory;
using Xunit;

namespace ApTutor.Tests;

// "explain it to me" experiment (explain-it-to-me-experiment branch only — see its own handoff) —
// TutorChatService's conversation-history bookkeeping, exercised against a fake HttpMessageHandler
// (same pattern as ClaudeClientTests) so no real network call or API key is ever needed.
public class TutorChatServiceTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = new();
        private readonly Func<int, string> _respond;

        public FakeHandler(Func<int, string> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            RequestBodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respond(RequestBodies.Count), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string TextResponse(string text) =>
        $$"""{ "content": [ { "type": "text", "text": "{{text}}" } ] }""";

    [Fact]
    public async Task ExplainAsync_SendsAnImplicitExplainRequest_ReturnsTheReply()
    {
        var handler = new FakeHandler(_ => TextResponse("Velocity is speed with direction."));
        var client = new ClaudeClient("fake-key", "fake-model", handler);
        var chat = new TutorChatService(client, "Velocity", "Velocity combines speed and direction.");

        var reply = await chat.ExplainAsync();

        Assert.Equal("Velocity is speed with direction.", reply);
        var body = JsonNode.Parse(handler.RequestBodies[0])!;
        Assert.Equal("Explain this lesson to me.", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task SystemPrompt_GroundsInTheGivenLessonContent()
    {
        var handler = new FakeHandler(_ => TextResponse("reply"));
        var client = new ClaudeClient("fake-key", "fake-model", handler);
        var chat = new TutorChatService(client, "Sign Conventions", "If velocity and acceleration share a sign, speed increases.");

        await chat.ExplainAsync();

        var body = JsonNode.Parse(handler.RequestBodies[0])!;
        var system = body["system"]!.GetValue<string>();
        Assert.Contains("Sign Conventions", system);
        Assert.Contains("If velocity and acceleration share a sign, speed increases.", system);
    }

    [Fact]
    public async Task AskFollowUpAsync_IncludesPriorTurnsInTheRequest()
    {
        var handler = new FakeHandler(i => TextResponse(i == 1 ? "Speed with direction." : "An example: a car going 60mph north."));
        var client = new ClaudeClient("fake-key", "fake-model", handler);
        var chat = new TutorChatService(client, "Velocity", "lesson content");

        await chat.ExplainAsync();
        await chat.AskFollowUpAsync("Can you give an example?");

        var secondRequestBody = JsonNode.Parse(handler.RequestBodies[1])!;
        var messages = secondRequestBody["messages"]!.AsArray();
        Assert.Equal(3, messages.Count); // explain request, first reply, follow-up question
        Assert.Equal("assistant", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("Speed with direction.", messages[1]!["content"]!.GetValue<string>());
        Assert.Equal("Can you give an example?", messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task History_AccumulatesAcrossTurns()
    {
        var handler = new FakeHandler(_ => TextResponse("reply"));
        var client = new ClaudeClient("fake-key", "fake-model", handler);
        var chat = new TutorChatService(client, "Velocity", "lesson content");

        await chat.ExplainAsync();
        await chat.AskFollowUpAsync("follow-up");

        Assert.Equal(4, chat.History.Count); // (user, assistant) x 2 turns
    }

    [Fact]
    public async Task SendAsync_OnFailure_DoesNotLeaveADanglingUserTurnInHistory()
    {
        var handler = new FakeHandler(_ => """{"content": []}""");
        var client = new ClaudeClient("bad-key", "fake-model", handler);
        var chat = new TutorChatService(client, "Velocity", "lesson content");

        // HTTP 200 with no text block — exercises SendMessageAsync's own "no text block in
        // response" InvalidOperationException path (simpler to trigger here than standing up a
        // second fake for a non-200 status, and this test only cares about history bookkeeping on
        // any failure, not which failure).
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.ExplainAsync());

        Assert.Empty(chat.History);
    }
}
