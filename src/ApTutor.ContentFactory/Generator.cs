using System.Text.Json;
using System.Text.Json.Serialization;
using ApTutor.Content;
using ApTutor.Curriculum;
using ApTutor.Platform;
using ApTutor.Scene;

namespace ApTutor.ContentFactory;

// The model-generated shape: identity fields (item id, node id, step index) are assigned by us
// afterward, not left for the model to invent — those are structural, not content.
//
// GeneratedChoice (see the graph-spec-rendering plan's Part 2) is a discriminated union over the
// wire: Kind picks which of Text/Graph is meaningful. The model is required to state Kind
// explicitly rather than us guessing it from which field is present — the same "never trust the
// model with structural facts it could get subtly wrong" posture as every other Generated* shape
// in this file. Real validation (kind matches a populated field, a graph is actually well-formed)
// happens in Generator.GenerateAsync, not here.
//
// GeneratedChoiceConverter also accepts a bare JSON string with no "kind" wrapper at all — that's
// exactly what GenerationSchema.PracticeItemsArraySchema still asks for when allowGraphChoices is
// false (the unchanged, pre-graph-spec choice shape), so ordinary text-only generation keeps
// deserializing into this same type without the caller needing to know or care which wire shape
// Claude actually returned.
[JsonConverter(typeof(GeneratedChoiceConverter))]
public sealed record GeneratedChoice(string Kind, string? Text, GraphSpec? Graph);

public sealed class GeneratedChoiceConverter : JsonConverter<GeneratedChoice>
{
    public override GeneratedChoice Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new GeneratedChoice("text", reader.GetString(), null);

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var kind = root.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "" : "";
        var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString()
            : null;
        var graph = root.TryGetProperty("graph", out var graphEl) && graphEl.ValueKind == JsonValueKind.Object
            ? graphEl.Deserialize<GraphSpec>(options)
            : null;
        return new GeneratedChoice(kind, text, graph);
    }

    public override void Write(Utf8JsonWriter writer, GeneratedChoice value, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(GeneratedChoice)} is only ever parsed from Claude's response, never written.");
}

public sealed record GeneratedPracticeItem(string Prompt, IReadOnlyList<GeneratedChoice> Choices, int CorrectIndex, string Explanation);
public sealed record GeneratedStep(string Caption, int? SourceLine, IReadOnlyList<SceneOp> Ops);
public sealed record GeneratedNodeContent(
    string WalkthroughText, IReadOnlyList<GeneratedPracticeItem> PracticeItems, IReadOnlyList<GeneratedStep> WalkthroughSteps);
// Same defensive posture as GeneratedChoiceConverter above: the schema asks for {caption,detail}
// objects, but a model asked for "steps" naturally sometimes emits a plain array of strings instead
// (each string standing in as the caption) — accept both wire shapes rather than failing generation
// over a formatting choice, same as every other Generated* converter in this file.
[JsonConverter(typeof(GeneratedLearnStepConverter))]
public sealed record GeneratedLearnStep(string Caption, string? Detail);

public sealed class GeneratedLearnStepConverter : JsonConverter<GeneratedLearnStep>
{
    public override GeneratedLearnStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new GeneratedLearnStep(reader.GetString() ?? "", null);

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var caption = root.TryGetProperty("caption", out var captionEl) && captionEl.ValueKind == JsonValueKind.String
            ? captionEl.GetString() ?? ""
            : "";
        var detail = root.TryGetProperty("detail", out var detailEl) && detailEl.ValueKind == JsonValueKind.String
            ? detailEl.GetString()
            : null;
        return new GeneratedLearnStep(caption, detail);
    }

    public override void Write(Utf8JsonWriter writer, GeneratedLearnStep value, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(GeneratedLearnStep)} is only ever parsed from Claude's response, never written.");
}

// The schema asks for "steps" to be an array (minItems: 2), but a model that generates only one
// step sometimes drops the array wrapper entirely and returns a single step object/string directly
// — accept that too rather than failing generation, same posture as GeneratedLearnStepConverter.
public sealed class GeneratedLearnStepListConverter : JsonConverter<IReadOnlyList<GeneratedLearnStep>>
{
    public override IReadOnlyList<GeneratedLearnStep> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.StartArray
            ? JsonSerializer.Deserialize<List<GeneratedLearnStep>>(ref reader, options) ?? new List<GeneratedLearnStep>()
            : new List<GeneratedLearnStep> { JsonSerializer.Deserialize<GeneratedLearnStep>(ref reader, options)! };

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<GeneratedLearnStep> value, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(GeneratedLearnStep)} lists are only ever parsed from Claude's response, never written.");
}

public sealed record GeneratedLearnContent(
    string Overview,
    [property: JsonConverter(typeof(GeneratedLearnStepListConverter))] IReadOnlyList<GeneratedLearnStep> Steps);
public sealed record GeneratedUnitNode(string Id, string Title, string Type, IReadOnlyList<string> Prereqs, string Viz);
public sealed record GeneratedUnitStructure(IReadOnlyList<GeneratedUnitNode> Nodes);
public sealed record GeneratedCourseUnit(int Unit, string Title);
public sealed record GeneratedCourseUnitList(IReadOnlyList<GeneratedCourseUnit> Units);

public sealed class Generator
{
    // CamelCase + case-insensitive so "frameId" (our schema/prompt) maps onto SceneOp's "FrameId"
    // etc., reusing the same [JsonPolymorphic]/[JsonDerivedType] "op" discriminator that
    // ApTutor.Scene already declares — no hand-written op-shape mapping needed. JsonStringEnumConverter
    // is needed for GraphAxisKind (the schema asks for "x"/"y" strings, not numbers) — it's the
    // first enum-typed field any Generated* shape has ever had; every other enum in this file
    // (NodeType, Difficulty) is parsed as a plain string and hand-converted via Enum.TryParse
    // instead, so adding this converter can't change how any of those already behave.
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ClaudeClient _client;

    public Generator(ClaudeClient client) => _client = client;

    /// allowGraphChoices is the real, explicit signal that this generation's answer options may be
    /// static line/point graphs instead of plain text (see the graph-spec-rendering plan's Part 2)
    /// — required, not defaulted, so every call site states its intent rather than the prompt
    /// vaguely hoping the model infers it. False preserves today's text-only generation exactly;
    /// existing courses/nodes are entirely unaffected unless a caller opts in.
    public async Task<NodeContentPack> GenerateAsync(string courseId, DagNode node, Difficulty difficulty, bool allowGraphChoices, CancellationToken ct = default)
    {
        var schema = GenerationSchema.NodeContentSchema(allowGraphChoices);
        var system = PromptTemplates.System(courseId);
        var user = PromptTemplates.ForNode(node, difficulty, allowGraphChoices);

        var inputJson = await _client.GenerateToolInputAsync(system, user, schema, "emit_node_content", ct);
        var generated = JsonSerializer.Deserialize<GeneratedNodeContent>(inputJson.GetRawText(), ParseOptions)
            ?? throw new InvalidOperationException($"Claude returned an empty/unparsable content block for node '{node.Id}'.");

        var practiceItems = generated.PracticeItems
            .Select((item, i) => new PracticeItem(
                $"{node.Id}-q{i + 1}", node.Id, item.Prompt,
                item.Choices.Select((choice, ci) => ToPracticeItemChoice(choice, node.Id, i, ci)).ToList(),
                item.CorrectIndex, item.Explanation))
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

    /// Turns one raw generated choice into a real PracticeItemChoice, failing loudly on anything
    /// that doesn't parse as a genuinely well-formed choice — "the JSON parses" and "the JSON
    /// correctly represents what was asked for" are different bars (see the graph-spec-rendering
    /// plan's Part 5), and this is the boundary that catches the first one before a malformed draft
    /// ever reaches an SME's review screen. A validation failure here fails the whole generation
    /// (see ContentGenerationService.RunAsync's existing catch-and-fail handling) exactly like any
    /// other bad generation — never silently downgraded to a broken or half-populated choice.
    private static PracticeItemChoice ToPracticeItemChoice(GeneratedChoice choice, string nodeId, int itemIndex, int choiceIndex)
    {
        var where = $"node '{nodeId}' item {itemIndex + 1} choice {choiceIndex + 1}";
        return choice.Kind?.ToLowerInvariant() switch
        {
            "text" => !string.IsNullOrWhiteSpace(choice.Text)
                ? PracticeItemChoice.OfText(choice.Text)
                : throw new InvalidOperationException($"{where}: kind is 'text' but no text was provided."),
            "graph" => choice.Graph is { } graph
                ? PracticeItemChoice.OfGraph(ValidateGraph(graph, where))
                : throw new InvalidOperationException($"{where}: kind is 'graph' but no graph was provided."),
            _ => throw new InvalidOperationException($"{where}: unrecognized choice kind '{choice.Kind}'."),
        };
    }

    /// Structural sanity, not physics correctness (see the graph-spec-rendering plan's Part 5 —
    /// "the JSON correctly represents the physics" is a separate, iterative concern this method
    /// can't judge). This only catches a graph that couldn't possibly render sensibly: missing axis
    /// labels, no segments at all, or a segment too short to draw a line from.
    private static GraphSpec ValidateGraph(GraphSpec graph, string where)
    {
        if (string.IsNullOrWhiteSpace(graph.XAxis.Label))
            throw new InvalidOperationException($"{where}: graph is missing an X axis label.");
        if (string.IsNullOrWhiteSpace(graph.YAxis.Label))
            throw new InvalidOperationException($"{where}: graph is missing a Y axis label.");
        if (graph.Segments.Count == 0)
            throw new InvalidOperationException($"{where}: graph has zero line segments.");
        foreach (var segment in graph.Segments)
            if (segment.Points.Count < 2)
                throw new InvalidOperationException($"{where}: a graph segment needs at least 2 points to draw a line, got {segment.Points.Count}.");
        if (graph.ReferenceValues is { } refs)
            foreach (var reference in refs)
                if (string.IsNullOrWhiteSpace(reference.Label))
                    throw new InvalidOperationException($"{where}: a graph reference value is missing its label.");

        return graph;
    }

    /// Content Admin's Learn-mode teaching content (see the learn-quiz-mode-switch plan's Part B) —
    /// not difficulty-scoped and not tied to any allow-graph-choices toggle; a node has exactly one
    /// current explanation, reused across every difficulty (see S3ContentStore.ApproveLearnContentAsync's
    /// remarks on why this isn't content-addressed like practice content).
    public async Task<LearnContent> GenerateLearnContentAsync(string courseId, DagNode node, CancellationToken ct = default)
    {
        var schema = GenerationSchema.LearnContentSchema();
        var system = PromptTemplates.System(courseId);
        var user = PromptTemplates.ForLearnContent(node);

        var inputJson = await _client.GenerateToolInputAsync(system, user, schema, "emit_learn_content", ct);
        var generated = JsonSerializer.Deserialize<GeneratedLearnContent>(inputJson.GetRawText(), ParseOptions)
            ?? throw new InvalidOperationException($"Claude returned an empty/unparsable learn-content block for node '{node.Id}'.");

        if (string.IsNullOrWhiteSpace(generated.Overview))
            throw new InvalidOperationException($"Node '{node.Id}': learn content is missing an overview.");
        if (generated.Steps.Count == 0)
            throw new InvalidOperationException($"Node '{node.Id}': learn content has zero steps.");
        foreach (var step in generated.Steps)
            if (string.IsNullOrWhiteSpace(step.Caption))
                throw new InvalidOperationException($"Node '{node.Id}': a learn-content step is missing its caption.");

        var steps = generated.Steps.Select(s => new LearnStep(s.Caption, s.Detail)).ToList();
        return new LearnContent(generated.Overview, steps, Verified: false, DateTimeOffset.UtcNow, _client.Model);
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
