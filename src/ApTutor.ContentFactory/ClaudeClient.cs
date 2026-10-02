// Thin wrapper around the Anthropic Messages API. Historically build-time-only (Content
// Admin/ApTutor.ContentFactory), using forced tool-use (tool_choice pinned to one tool) so the
// model's output is a single schema-shaped JSON object, not prose we'd have to scrape — see
// GenerateToolInputAsync. The "explain it to me" experiment (explain-it-to-me-experiment branch
// only — see its own handoff) is the first caller to use this live from the Shell, via
// SendMessageAsync's plain conversational path below; that's a deliberate, scoped exception for
// this experiment, not a change to how the rest of the app's generation pipeline works.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApTutor.ContentFactory;

public sealed class ClaudeClient
{
    private readonly HttpClient _http;

    public string Model { get; }

    /// handler is injectable so tests can fake the HTTP call without hitting the network or
    /// needing a real API key.
    public ClaudeClient(string apiKey, string model, HttpMessageHandler? handler = null)
    {
        Model = model;
        _http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        _http.BaseAddress = new Uri("https://api.anthropic.com/");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    /// Sends one user message with a single forced tool, and returns that tool call's "input"
    /// object — the model's entire response, structured to the given JSON schema.
    public async Task<JsonElement> GenerateToolInputAsync(
        string system, string userPrompt, JsonNode inputSchema, string toolName, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = 8192,
            ["system"] = system,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = userPrompt } },
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = toolName,
                    ["description"] = $"Emit the generated node content as {toolName}.",
                    ["input_schema"] = inputSchema,
                },
            },
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = toolName },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Claude API returned {(int)response.StatusCode}: {responseText}");

        using var doc = JsonDocument.Parse(responseText);
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "tool_use" &&
                block.TryGetProperty("name", out var name) && name.GetString() == toolName)
            {
                return block.GetProperty("input").Clone(); // Clone: doc is disposed on return
            }
        }

        throw new InvalidOperationException($"Claude response contained no '{toolName}' tool_use block:\n{responseText}");
    }

    /// Plain conversational turn — no forced tool, no schema — for the "explain it to me" experiment
    /// (see this file's own remarks). history is the whole conversation so far, in order, each a
    /// ("user"|"assistant", text) pair; the caller is responsible for appending the new user turn
    /// before calling this and the returned assistant turn after. Returns every text block in the
    /// response joined together — plain conversational replies are effectively always a single text
    /// block, but joining defensively covers the rare case where the model splits its reply.
    public async Task<string> SendMessageAsync(
        string system, IReadOnlyList<(string Role, string Text)> history, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = 1024,
            ["system"] = system,
            ["messages"] = new JsonArray(history.Select(turn => (JsonNode)new JsonObject
            {
                ["role"] = turn.Role,
                ["content"] = turn.Text,
            }).ToArray()),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Claude API returned {(int)response.StatusCode}: {responseText}");

        using var doc = JsonDocument.Parse(responseText);
        var textBlocks = doc.RootElement.GetProperty("content").EnumerateArray()
            .Where(block => block.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(block => block.GetProperty("text").GetString() ?? "")
            .ToList();

        return textBlocks.Count > 0
            ? string.Join("\n", textBlocks)
            : throw new InvalidOperationException($"Claude response contained no text block:\n{responseText}");
    }
}
