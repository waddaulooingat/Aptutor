// Composition-root wiring for the CS A course. This is the one place allowed to know "CS A"
// exists — the shell (MainWindow) talks only to ICourseModule/CourseRegistry (build-plan Phase 1
// acceptance: "no course-specific code in the shell"). StepProvider is wired to the real Phase 3
// tracer as of Phase 4; Content stays stubbed until Phase 5 (content integration).

using ApTutor.Curriculum;
using ApTutor.Platform;
using ApTutor.Tracer;

namespace ApTutor.Client.Courses;

public sealed class CsaCourseModule : ICourseModule
{
    public string CourseId => "csa";
    public string DisplayName => "Computer Science A";
    public SkillGraph Dag { get; }

    public IReadOnlyList<IScenePrimitiveRenderer> Primitives => Array.Empty<IScenePrimitiveRenderer>();
    public IStepProvider StepProvider { get; } = new TracerStepProvider();
    public IContentSource Content { get; } = new StubContentSource();
    public IAttemptGrader? Grader => null;

    public CsaCourseModule(string dagJsonPath) => Dag = SkillDagLoader.Load(dagJsonPath);
}

/// build-plan.md Phase 3: "wire it as the CS A module's IStepProvider with SupportsLiveInput =
/// true." GetStepsForInput traces arbitrary Java live; GetSteps serves pre-authored examples by
/// (nodeId, exampleId) — for now that's just the reference-vs-value walkthrough on u2.1 ("Objects
/// as instances of classes; reference vs. primitive"), the DAG node it's actually teaching. A real
/// per-node example bank is Phase 5 (content integration), not this phase's job.
file sealed class TracerStepProvider : IStepProvider
{
    public bool SupportsLiveInput => true;

    public IReadOnlyList<VisualStep> GetSteps(string nodeId, string exampleId) => nodeId switch
    {
        "u2.1" => Trace(SampleProgram.ReferenceVsValueDemoJava),
        _ => throw new NotImplementedException($"No authored example for node '{nodeId}' yet (Phase 5: content integration)."),
    };

    public IReadOnlyList<VisualStep> GetStepsForInput(string nodeId, string userInput) => Trace(userInput);

    private static IReadOnlyList<VisualStep> Trace(string javaSource)
    {
        var result = new Tracer.Tracer().Trace(javaSource);
        if (!result.Completed)
            throw new InvalidOperationException(result.HaltReason ?? "Tracing failed.");
        return result.Steps.Select(s => new VisualStep(s.Index, s.Caption, s.Delta, s.SourceLine)).ToList();
    }
}

file sealed class StubContentSource : IContentSource
{
    public string GetWalkthroughText(string nodeId, string exampleId) =>
        throw new NotImplementedException("Wired in Phase 5 (content integration).");

    public IReadOnlyList<PracticeItem> GetPracticeItems(string nodeId) =>
        throw new NotImplementedException("Wired in Phase 5 (content integration).");
}
