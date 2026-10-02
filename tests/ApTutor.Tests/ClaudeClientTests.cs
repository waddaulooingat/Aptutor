using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ApTutor.ContentFactory;
using Xunit;

namespace ApTutor.Tests;

// Phase 7: ClaudeClient never touches the real network in tests — no API key, no spend. A fake
// HttpMessageHandler stands in for the Anthropic API so the request-shaping and
// tool_use-response-parsing logic is verified without needing a live key.
public class ClaudeClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        private readonly Func<string, HttpResponseMessage> _respond;

        public FakeHandler(Func<string, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _respond(LastRequestBody ?? "");
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task GenerateToolInputAsync_ParsesToolUseInputFromResponse()
    {
        const string responseJson = """
            {
                "content": [
                    { "type": "text", "text": "some preamble the model might add" },
                    { "type": "tool_use", "id": "toolu_1", "name": "emit_node_content",
                      "input": { "walkthroughText": "hello", "practiceItems": [], "walkthroughSteps": [] } }
                ]
            }
            """;
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.OK, responseJson));
        var client = new ClaudeClient("fake-key", "fake-model", handler);

        var input = await client.GenerateToolInputAsync("system", "user", new JsonObject { ["type"] = "object" }, "emit_node_content");

        Assert.Equal("hello", input.GetProperty("walkthroughText").GetString());
    }

    [Fact]
    public async Task GenerateToolInputAsync_SendsCorrectHeadersAndForcedToolChoice()
    {
        const string responseJson = """
            { "content": [ { "type": "tool_use", "name": "emit_node_content", "input": {} } ] }
            """;
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.OK, responseJson));
        var client = new ClaudeClient("my-api-key", "claude-test-model", handler);

        await client.GenerateToolInputAsync("sys", "user prompt", new JsonObject { ["type"] = "object" }, "emit_node_content");

        Assert.Equal("my-api-key", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());

        var body = JsonNode.Parse(handler.LastRequestBody!)!;
        Assert.Equal("claude-test-model", body["model"]!.GetValue<string>());
        Assert.Equal("emit_node_content", body["tool_choice"]!["name"]!.GetValue<string>());
        Assert.Equal("tool", body["tool_choice"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task GenerateToolInputAsync_NonSuccessStatus_ThrowsWithBody()
    {
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.Unauthorized, """{"error":"bad key"}"""));
        var client = new ClaudeClient("bad-key", "model", handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateToolInputAsync("sys", "user", new JsonObject { ["type"] = "object" }, "emit_node_content"));

        Assert.Contains("bad key", ex.Message);
    }

    [Fact]
    public async Task GenerateToolInputAsync_NoMatchingToolUseBlock_Throws()
    {
        const string responseJson = """{ "content": [ { "type": "text", "text": "no tool call here" } ] }""";
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.OK, responseJson));
        var client = new ClaudeClient("key", "model", handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateToolInputAsync("sys", "user", new JsonObject { ["type"] = "object" }, "emit_node_content"));
    }

    // SendMessageAsync — the "explain it to me" experiment's plain conversational path (see its own
    // remarks): no forced tool, no schema, just a text reply.
    [Fact]
    public async Task SendMessageAsync_ReturnsTextBlockContent()
    {
        const string responseJson = """{ "content": [ { "type": "text", "text": "Velocity is how fast something moves, with direction." } ] }""";
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.OK, responseJson));
        var client = new ClaudeClient("fake-key", "fake-model", handler);

        var reply = await client.SendMessageAsync("system prompt", new[] { ("user", "Explain this lesson to me.") });

        Assert.Equal("Velocity is how fast something moves, with direction.", reply);
    }

    [Fact]
    public async Task SendMessageAsync_MultipleTextBlocks_JoinsThemTogether()
    {
        const string responseJson = """{ "content": [ { "type": "text", "text": "First part." }, { "type": "text", "text": "Second part." } ] }""";
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.OK, responseJson));
        var client = new ClaudeClient("fake-key", "fake-model", handler);

        var reply = await client.SendMessageAsync("system", new[] { ("user", "question") });

        Assert.Equal("First part.\nSecond part.", reply);
    }

    [Fact]
    public async Task SendMessageAsync_SendsFullHistoryNotJustTheLatestTurn()
    {
        const string responseJson = """{ "content": [ { "type": "text", "text": "reply" } ] }""";
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.OK, responseJson));
        var client = new ClaudeClient("fake-key", "fake-model", handler);

        await client.SendMessageAsync("system", new[]
        {
            ("user", "What is velocity?"),
            ("assistant", "Speed with direction."),
            ("user", "Can you give an example?"),
        });

        var body = JsonNode.Parse(handler.LastRequestBody!)!;
        var messages = body["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("Can you give an example?", messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task SendMessageAsync_NonSuccessStatus_ThrowsWithBody()
    {
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.Unauthorized, """{"error":"bad key"}"""));
        var client = new ClaudeClient("bad-key", "model", handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendMessageAsync("system", new[] { ("user", "hi") }));

        Assert.Contains("bad key", ex.Message);
    }

    [Fact]
    public async Task SendMessageAsync_NoTextBlockInResponse_Throws()
    {
        const string responseJson = """{ "content": [] }""";
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.OK, responseJson));
        var client = new ClaudeClient("key", "model", handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendMessageAsync("system", new[] { ("user", "hi") }));
    }
}
