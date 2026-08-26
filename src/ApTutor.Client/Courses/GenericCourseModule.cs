// Generic composition-root wiring for any course with no bespoke interactive engineering — no live
// tracer, no special-cased demo node, no fixture item bank. StepProvider and Content are the
// course-agnostic AuthoredStepProvider/FileContentSource wired directly at a content directory, the
// same way WorldHistoryCourseModule always worked (see the Shell-display-only/course-authoring
// plan, which retired that course-specific class in favor of this one — World History never had any
// logic beyond what this class already provides).
//
// Unlike CsaCourseModule, this takes an already-loaded SkillGraph rather than a file path: any
// course reaching this class was discovered from S3 (see CourseDiscoveryService), not read from a
// file baked into the build.

using ApTutor.Content;
using ApTutor.Curriculum;
using ApTutor.Platform;

namespace ApTutor.Client.Courses;

public sealed class GenericCourseModule : ICourseModule
{
    public string CourseId { get; }
    public string DisplayName { get; }
    public SkillGraph Dag { get; }

    public IReadOnlyList<IScenePrimitiveRenderer> Primitives => Array.Empty<IScenePrimitiveRenderer>();
    public IStepProvider StepProvider { get; }
    public IContentSource Content { get; }
    public IAttemptGrader? Grader => null;
    public string ContentDir { get; }

    /// displayName should already be trademark-compliant (no "AP"/"Advanced Placement") — see
    /// DagMeta's remarks; this class does no filtering of its own, it trusts the caller.
    public GenericCourseModule(string courseId, string displayName, SkillGraph dag, string contentDir)
    {
        CourseId = courseId;
        DisplayName = displayName;
        Dag = dag;
        ContentDir = contentDir;
        StepProvider = new AuthoredStepProvider(contentDir);
        Content = new FileContentSource(contentDir);
    }
}
