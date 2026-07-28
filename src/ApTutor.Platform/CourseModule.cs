// Advanced Test Prepper — Platform contract.
// Target: .NET 8, C# 12, nullable enabled.
// This is the seam that makes courses modular. Each course (CS A, Physics, Chem, ...) ships as
// an ICourseModule. The shell discovers modules, builds the left rail from them, and drives
// EVERYTHING through the shared SceneDelta protocol — so stepping, scrubbing, TTS, mastery, and
// reporting are course-agnostic and built exactly once. Adding a course = adding a module,
// not touching the platform.

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
                                  IReadOnlyList<string> Choices, int CorrectIndex, string Explanation);

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
