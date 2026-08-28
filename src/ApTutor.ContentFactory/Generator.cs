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
public sealed record GeneratedUnitNode(string Id, string Title, string Type, IReadOnlyList<string> Prereqs, string Viz);
public sealed record GeneratedUnitStructure(IReadOnlyList<GeneratedUnitNode> Nodes);
public sealed record GeneratedCourseUnit(int Unit, string Title);
public sealed record GeneratedCourseUnitList(IReadOnlyList<GeneratedCourseUnit> Units);

public sealed class Generator
{
    // CamelCase + case-insensitive so "frameId" (our schema/prompt) maps onto SceneOp's "FrameId"
    // etc., reusing the same [JsonPolymorphic]/[JsonDerivedType] "op" discriminator that
    // ApTutor.Scene already declares — no hand-written op-shape mapping needed.
    private static readonly JsonSerializerOptions ParseOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ClaudeClient _client;

    public Generator(ClaudeClient client) => _client = client;

    public async Task<NodeContentPack> GenerateAsync(string courseId, DagNode node, Difficulty difficulty, CancellationToken ct = default)
    {
        var schema = GenerationSchema.NodeContentSchema();
        var system = PromptTemplates.System(courseId);
        var user = PromptTemplates.ForNode(node, difficulty);

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
            Model: _client.Model,
            Difficulty: difficulty);
    }

    /// Content Admin's "Generate unit structure" (see the Shell-display-only/course-authoring
    /// plan's Part B) — one level above per-node content generation: drafts the node list
    /// (ids/titles/types/prereqs) for a unit, not any node's actual explanation/practice items.
    /// existingNodes gives the model real node ids it can reference in prereqs. The model is never
    /// trusted with the Unit field itself (assigned here from the caller's unitNumber, not parsed
    /// from its output); id-prefix and duplicate-id checks happen here, but full prereq/cycle
    /// validation across the whole merged course happens later, in StructureMerge + the SkillGraph
    /// constructor it feeds — this method alone can't know about nodes outside this one unit.
    public async Task<IReadOnlyList<DagNode>> GenerateUnitStructureAsync(
        string courseId, int unit, string unitTitle, IReadOnlyList<DagNode> existingNodes, string? guidance, CancellationToken ct = default)
    {
        var schema = GenerationSchema.UnitStructureSchema();
        var system = PromptTemplates.System(courseId);
        var user = PromptTemplates.ForUnit(unit, unitTitle, existingNodes, guidance);

        var inputJson = await _client.GenerateToolInputAsync(system, user, schema, "emit_unit_structure", ct);
        var generated = JsonSerializer.Deserialize<GeneratedUnitStructure>(inputJson.GetRawText(), ParseOptions)
            ?? throw new InvalidOperationException($"Claude returned an empty/unparsable structure block for unit {unit}.");

        if (generated.Nodes.Count == 0)
            throw new InvalidOperationException($"Claude returned zero nodes for unit {unit}.");

        var idPrefix = $"u{unit}.";
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DagNode>(generated.Nodes.Count);

        foreach (var raw in generated.Nodes)
        {
            if (!raw.Id.StartsWith(idPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Generated node id '{raw.Id}' doesn't start with the required '{idPrefix}' prefix for unit {unit}.");
            if (!seenIds.Add(raw.Id))
                throw new InvalidOperationException($"Claude generated a duplicate node id '{raw.Id}' within the same unit.");
            if (!Enum.TryParse<NodeType>(raw.Type, ignoreCase: true, out var type))
                throw new InvalidOperationException($"Generated node '{raw.Id}' has an unrecognized type '{raw.Type}'.");

            result.Add(new DagNode(raw.Id, unit, type, raw.Title, raw.Prereqs, raw.Viz));
        }

        return result;
    }

    /// Content Admin's "Create new course" (see the Shell-display-only/course-authoring plan's
    /// Part C) — the top-level entry point, one level above GenerateUnitStructureAsync: drafts only
    /// the unit list (a course's table of contents), never any unit's node structure.
    public async Task<IReadOnlyList<UnitInfo>> GenerateCourseUnitListAsync(string courseId, string courseName, string? guidance, CancellationToken ct = default)
    {
        var schema = GenerationSchema.CourseUnitListSchema();
        var system = PromptTemplates.System(courseId);
        var user = PromptTemplates.ForCourse(courseName, guidance);

        var inputJson = await _client.GenerateToolInputAsync(system, user, schema, "emit_course_units", ct);
        var generated = JsonSerializer.Deserialize<GeneratedCourseUnitList>(inputJson.GetRawText(), ParseOptions)
            ?? throw new InvalidOperationException("Claude returned an empty/unparsable unit-list block.");

        if (generated.Units.Count == 0)
            throw new InvalidOperationException("Claude returned zero units for the new course.");

        var seenUnitNumbers = new HashSet<int>();
        var result = new List<UnitInfo>(generated.Units.Count);
        foreach (var raw in generated.Units)
        {
            if (!seenUnitNumbers.Add(raw.Unit))
                throw new InvalidOperationException($"Claude generated a duplicate unit number {raw.Unit}.");

            result.Add(new UnitInfo(raw.Unit, raw.Title));
        }

        return result.OrderBy(u => u.Unit).ToList();
    }
}
