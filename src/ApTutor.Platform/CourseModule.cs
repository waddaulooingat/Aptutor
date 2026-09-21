// Advanced Test Prepper — Platform contract.
// Target: .NET 8, C# 12, nullable enabled.
// This is the seam that makes courses modular. Each course (CS A, Physics, Chem, ...) ships as
// an ICourseModule. The shell discovers modules, builds the left rail from them, and drives
// EVERYTHING through the shared SceneDelta protocol — so stepping, scrubbing, TTS, mastery, and
// reporting are course-agnostic and built exactly once. Adding a course = adding a module,
// not touching the platform.

using System.Text.Json;
using System.Text.Json.Serialization;
using ApTutor.Curriculum; // SkillGraph, DagNode
using ApTutor.Scene;      // SceneOp, SceneDelta

namespace ApTutor.Platform;

// ---------- Platform-level step (decoupled from any course's engine) ----------

/// One scrubable step of a walkthrough. Whatever produced it — the CS A Java tracer, a physics
/// simulator, or an author replaying a canned animation — the platform only sees VisualStep.
public sealed record VisualStep(int Index, string Caption, SceneDelta Delta, int? SourceLine = null);

// ---------- The module contract ----------

public interface ICourseModule
{
    string CourseId { get; }     // "csa", "physics1", "chem" — stable key
    string DisplayName { get; }  // "Computer Science A" — brand copy only; AP referenced descriptively elsewhere
    SkillGraph Dag { get; }      // the course's skill DAG (loaded from its JSON)

    /// Domain-specific primitives this course registers with the scene renderer
    /// (CS A: memCell/refArrow/heapObject/callTree; Physics: vectors/free-body/motion-graphs).
    IReadOnlyList<IScenePrimitiveRenderer> Primitives { get; }

    IStepProvider StepProvider { get; }   // turns a lesson/example into VisualStep[]
    IContentSource Content { get; }       // verified item bank + authored walkthroughs
    IAttemptGrader? Grader { get; }       // optional rubric-based grading (FRQ-style)

    /// Where this course's content packs live on disk (what ApTutor.ContentFactory writes to and
    /// Content/StepProvider read from). Every current module is file-content-dir-based, so this
    /// lives on the shared contract rather than requiring a cast — needed by dev-only tooling that
    /// writes fresh (unverified) packs directly, bypassing the verified-only Content/StepProvider
    /// reading path on purpose.
    string ContentDir { get; }
}

/// Produces the step stream for a node's example.
public interface IStepProvider
{
    IReadOnlyList<VisualStep> GetSteps(string nodeId, string exampleId);

    /// True if this course can trace ARBITRARY student input live (CS A tracer),
    /// false if it only replays authored walkthroughs (Tier-1 launch courses).
    bool SupportsLiveInput { get; }

    /// Only meaningful when SupportsLiveInput; e.g. trace student-written Java.
    IReadOnlyList<VisualStep> GetStepsForInput(string nodeId, string userInput) =>
        throw new NotSupportedException("This module does not support live input.");
}

/// Serves verified, pre-generated content. Most runtime requests stop here (no Claude call).
public interface IContentSource
{
    string GetWalkthroughText(string nodeId, string exampleId);
    IReadOnlyList<PracticeItem> GetPracticeItems(string nodeId);
}

public sealed record PracticeItem(string Id, string NodeId, string Prompt,
                                  IReadOnlyList<PracticeItemChoice> Choices, int CorrectIndex, string Explanation);

/// One answer option — plain text (the overwhelming case today) or a static line/point graph (see
/// GraphSpec), for courses like Physics where an option is itself a graph, not text (e.g. four
/// lettered velocity-time plots). Exactly one of Text/Graph is meaningful per choice; IsGraph says
/// which. A single item's Choices can mix text and graph options — nothing here assumes a whole
/// question is one or the other (see the graph-spec-rendering plan).
[JsonConverter(typeof(PracticeItemChoiceConverter))]
public sealed record PracticeItemChoice(string? Text, GraphSpec? Graph)
{
    public bool IsGraph => Graph is not null;

    public static PracticeItemChoice OfText(string text) => new(text, null);
    public static PracticeItemChoice OfGraph(GraphSpec graph) => new(null, graph);
}

/// Reads either the current shape (<c>{"Text": "...", "Graph": null}</c>) or the bare-string shape
/// every choice was written in before graph-based choices existed
/// (<c>"int x = 5;"</c>) — real approved content already sits in S3 on the old shape, and there's no
/// one-time migration step; a choice written under the old shape simply upgrades in place the next
/// time its item is approved again. Always writes the current (object) shape.
public sealed class PracticeItemChoiceConverter : JsonConverter<PracticeItemChoice>
{
    public override PracticeItemChoice Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return PracticeItemChoice.OfText(reader.GetString()!);

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var text = root.TryGetProperty("Text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString()
            : null;
        var graph = root.TryGetProperty("Graph", out var graphEl) && graphEl.ValueKind == JsonValueKind.Object
            ? graphEl.Deserialize<GraphSpec>(options)
            : null;
        return new PracticeItemChoice(text, graph);
    }

    public override void Write(Utf8JsonWriter writer, PracticeItemChoice value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Text", value.Text);
        if (value.Graph is not null)
        {
            writer.WritePropertyName("Graph");
            JsonSerializer.Serialize(writer, value.Graph, options);
        }
        writer.WriteEndObject();
    }
}

/// Draws one domain primitive onto the scene canvas (Skia). The renderer dispatches SceneOps
/// to the primitive that owns them; new courses add primitives without touching the core loop.
public interface IScenePrimitiveRenderer
{
    string PrimitiveId { get; }              // "memCell", "vectorField", ...
    bool Handles(SceneOp op);
    void Draw(object canvas, SceneState state); // object canvas = SKCanvas; kept loose to avoid a hard Skia dep here
}

/// Grades a student attempt against a rubric; returns awarded points + per-item feedback.
public interface IAttemptGrader
{
    GradeResult Grade(string nodeId, string attempt);
}

public sealed record GradeResult(int Awarded, int Possible, IReadOnlyList<string> Feedback);

// ---------- Module registry (the shell talks to this, never to a specific course) ----------

public sealed class CourseRegistry
{
    private readonly Dictionary<string, ICourseModule> _modules = new(StringComparer.Ordinal);

    public void Register(ICourseModule module) => _modules[module.CourseId] = module;
    public IReadOnlyCollection<ICourseModule> All => _modules.Values;
    public ICourseModule Get(string courseId) => _modules[courseId];
}
