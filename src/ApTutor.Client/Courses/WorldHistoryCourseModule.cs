// Composition-root wiring for the World History pilot course — the second ICourseModule the shell
// can register, added to prove the multi-course architecture (BUILD-PLAN.md's "Course model"
// section) actually works for a course that isn't CS A. World History is a "generated-content
// course" in that section's taxonomy: no live engine, no CS-A-style tracer, no special-cased demo
// node — StepProvider and Content are Phase 7's course-agnostic AuthoredStepProvider/
// FileContentSource wired directly at a content directory, same as any future course would be.
// Content is pilot-scope only (one unit, see apwh-skill-dag.json's meta.purpose) and nothing has
// been generated for it yet — that's expected; ApTutor.ContentFactory generates it later, run by a
// human with their own Claude API key, exactly like CS A's Units 1-2 were.
//
// Every node's viz is "none": no history-appropriate scene primitives (timeline/map/cause-effect)
// exist yet, so there is nothing for IScenePrimitiveRenderer to draw for this course — building
// those primitives is explicitly out of scope for this pilot (BUILD-PLAN.md's shared primitive
// library is CS-A-flavored today; a generic timeline/map primitive would be a separate task).

using ApTutor.Content;
using ApTutor.Curriculum;
using ApTutor.Platform;

namespace ApTutor.Client.Courses;

public sealed class WorldHistoryCourseModule : ICourseModule
{
    public string CourseId => "worldhistory";

    // Trademark guardrail (BUILD-PLAN.md "Guardrails"): no "AP"/"Advanced Placement" in the
    // user-facing display name, mirroring CsaCourseModule.DisplayName ("Computer Science A", not
    // "AP Computer Science A"). "AP" is fine in code comments/docs (descriptive use only) and in
    // the DAG JSON's internal meta.course field, but never here.
    public string DisplayName => "World History";

    public SkillGraph Dag { get; }

    public IReadOnlyList<IScenePrimitiveRenderer> Primitives => Array.Empty<IScenePrimitiveRenderer>();
    public IStepProvider StepProvider { get; }
    public IContentSource Content { get; }
    public IAttemptGrader? Grader => null;

    /// contentDir defaults to "<app dir>/content/worldhistory" — where a human running
    /// `ApTutor.ContentFactory generate --course worldhistory ...` then `review` would produce
    /// verified "<nodeId>.json" packs. Overridable so tests can point at an isolated fixture
    /// directory. Unlike CsaCourseModule, there's no fixture bank layered on top and no tracer
    /// special-case: this course has no content yet, and both readers already handle that
    /// (FileContentSource returns an empty item list; AuthoredStepProvider throws a clear
    /// InvalidOperationException) without a wrapper needed here.
    public WorldHistoryCourseModule(string dagJsonPath, string? contentDir = null)
    {
        Dag = SkillDagLoader.Load(dagJsonPath);
        var dir = contentDir ?? Path.Combine(AppContext.BaseDirectory, "content", "worldhistory");
        StepProvider = new AuthoredStepProvider(dir);
        Content = new FileContentSource(dir);
    }
}
