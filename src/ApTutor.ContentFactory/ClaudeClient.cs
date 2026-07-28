// Thin wrapper around the Anthropic Messages API, used only at build time by this tool — never
// shipped in the client app (ApTutor.Client has no dependency on this project or on any API key).
// Uses forced tool-use (tool_choice pinned to one tool) so the model's output is a single
// schema-shaped JSON object, not prose we'd have to scrape.

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
}
