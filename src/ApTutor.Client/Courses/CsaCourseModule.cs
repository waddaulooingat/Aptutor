// Composition-root wiring for the CS A course. This is the one place allowed to know "CS A"
// exists — the shell (MainWindow) talks only to ICourseModule/CourseRegistry (build-plan Phase 1
// acceptance: "no course-specific code in the shell"). StepProvider/Content are stubbed until
// Phase 3 (tracer) and Phase 5 (content integration) wire them for real.

using ApTutor.Curriculum;
using ApTutor.Platform;

namespace ApTutor.Client.Courses;

public sealed class CsaCourseModule : ICourseModule
{
    public string CourseId => "csa";
    public string DisplayName => "Computer Science A";
    public SkillGraph Dag { get; }

    public IReadOnlyList<IScenePrimitiveRenderer> Primitives => Array.Empty<IScenePrimitiveRenderer>();
    public IStepProvider StepProvider { get; } = new StubStepProvider();
    public IContentSource Content { get; } = new StubContentSource();
    public IAttemptGrader? Grader => null;

    public CsaCourseModule(string dagJsonPath) => Dag = SkillDagLoader.Load(dagJsonPath);
}

file sealed class StubStepProvider : IStepProvider
{
    public bool SupportsLiveInput => false;

    public IReadOnlyList<VisualStep> GetSteps(string nodeId, string exampleId) =>
        throw new NotImplementedException("Wired in Phase 3 (tracer) / Phase 4 (walkthroughs).");
}

file sealed class StubContentSource : IContentSource
{
    public string GetWalkthroughText(string nodeId, string exampleId) =>
        throw new NotImplementedException("Wired in Phase 5 (content integration).");

    public IReadOnlyList<PracticeItem> GetPracticeItems(string nodeId) =>
        throw new NotImplementedException("Wired in Phase 5 (content integration).");
}
