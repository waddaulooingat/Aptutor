using System.Text.Json;
using ApTutor.Content;
using ApTutor.Curriculum;
using ApTutor.Platform;
using ApTutor.Scene;

namespace ApTutor.ContentFactory;

// The model-generated shape: identity fields (item id, node id, step index) are assigned by us
// afterward, not left for the model to invent — those are structural, not content.
public sealed record GeneratedPracticeItem(string Prompt, IReadOnlyList<string> Choices, int CorrectIndex, string Explanation);
public sealed record GeneratedStep(string Caption, int? SourceLine, IReadOnlyList<SceneOp> Ops);
public sealed record GeneratedNodeContent(
    string WalkthroughText, IReadOnlyList<GeneratedPracticeItem> PracticeItems, IReadOnlyList<GeneratedStep> WalkthroughSteps);
public sealed record GeneratedPracticeItemsOnly(IReadOnlyList<GeneratedPracticeItem> PracticeItems);

public sealed class Generator
{
    // CamelCase + case-insensitive so "frameId" (our schema/prompt) maps onto SceneOp's "FrameId"
    // etc., reusing the same [JsonPolymorphic]/[JsonDerivedType] "op" discriminator that
    // ApTutor.Scene already declares — no hand-written op-shape mapping needed.
    private static readonly JsonSerializerOptions ParseOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ClaudeClient _client;

    public Generator(ClaudeClient client) => _client = client;

    public async Task<NodeContentPack> GenerateAsync(string courseId, DagNode node, CancellationToken ct = default)
    {
        var schema = GenerationSchema.NodeContentSchema();
        var system = PromptTemplates.System(courseId);
        var user = PromptTemplates.ForNode(node);

        var inputJson = await _client.GenerateToolInputAsync(system, user, schema, "emit_node_content", ct);
        var generated = JsonSerializer.Deserialize<GeneratedNodeContent>(inputJson.GetRawText(), ParseOptions)
            ?? throw new InvalidOperationException($"Claude returned an empty/unparsable content block for node '{node.Id}'.");

        var practiceItems = generated.PracticeItems
            .Select((item, i) => new PracticeItem($"{node.Id}-q{i + 1}", node.Id, item.Prompt, item.Choices, item.CorrectIndex, item.Explanation))
            .ToList();

        var steps = generated.WalkthroughSteps
            .Select((step, i) => new VisualStep(i, step.Caption, new SceneDelta(step.Ops), step.SourceLine))
            .ToList();

        return new NodeContentPack(
            CourseId: courseId,
            NodeId: node.Id,
            ExampleId: "generated",
            WalkthroughText: generated.WalkthroughText,
            PracticeItems: practiceItems,
            WalkthroughSteps: steps,
            Verified: false,
            GeneratedAt: DateTimeOffset.UtcNow,
            Model: _client.Model);
    }

    /// Dev-only "refresh questions" (right-click a node in the shell): regenerates JUST that node's
    /// practice items — cheaper and faster than a full GenerateAsync when all you want is a fresh
    /// question bank, and it deliberately doesn't touch any existing walkthrough text/steps. Always
    /// unverified — the caller (MainWindow) saves it with Verified: false and a clear warning; this
    /// never writes into the same trusted path `review` approves without a human looking at it.
    public async Task<IReadOnlyList<PracticeItem>> RegeneratePracticeItemsAsync(string courseId, DagNode node, CancellationToken ct = default)
    {
        var schema = GenerationSchema.PracticeItemsOnlySchema();
        var system = PromptTemplates.System(courseId);
        var user = PromptTemplates.PracticeItemsOnlyForNode(node);

        var inputJson = await _client.GenerateToolInputAsync(system, user, schema, "emit_practice_items", ct);
        var generated = JsonSerializer.Deserialize<GeneratedPracticeItemsOnly>(inputJson.GetRawText(), ParseOptions)
            ?? throw new InvalidOperationException($"Claude returned an empty/unparsable practice-items block for node '{node.Id}'.");

        return generated.PracticeItems
            .Select((item, i) => new PracticeItem($"{node.Id}-q{i + 1}", node.Id, item.Prompt, item.Choices, item.CorrectIndex, item.Explanation))
            .ToList();
    }
}
