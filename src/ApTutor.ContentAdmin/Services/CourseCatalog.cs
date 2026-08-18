using ApTutor.Curriculum;

namespace ApTutor.ContentAdmin.Services;

public sealed record CourseInfo(string CourseId, string DisplayName, SkillGraph Graph);

/// Loads each configured course's skill DAG once, resolved against files already baked into this
/// app's own container image (AppContext.BaseDirectory) — DAG structure is curriculum code, not
/// generated content, and ships with the app the same way the desktop client already loads its own
/// DAG files. ApTutor.Curriculum's own `<Content Include>` items copy transitively into any
/// referencing project's build/publish output, so no extra wiring is needed here.
public sealed class CourseCatalog
{
    private readonly Dictionary<string, CourseInfo> _courses;

    public CourseCatalog(IConfiguration config)
    {
        _courses = new Dictionary<string, CourseInfo>(StringComparer.Ordinal);

        foreach (var courseSection in config.GetSection("ContentAdmin:Courses").GetChildren())
        {
            var courseId = courseSection.Key;
            var displayName = courseSection["DisplayName"] ?? courseId;
            var dagRelativePath = courseSection["DagPath"]
                ?? throw new InvalidOperationException($"Course '{courseId}' is missing DagPath in configuration.");

            var dagPath = Path.Combine(AppContext.BaseDirectory, dagRelativePath);
            var graph = SkillDagLoader.Load(dagPath);

            _courses[courseId] = new CourseInfo(courseId, displayName, graph);
        }
    }

    public IReadOnlyCollection<CourseInfo> All => _courses.Values;

    public CourseInfo Get(string courseId) =>
        _courses.TryGetValue(courseId, out var course)
            ? course
            : throw new KeyNotFoundException($"Unknown course '{courseId}'.");

    public bool TryGet(string courseId, out CourseInfo course) =>
        _courses.TryGetValue(courseId, out course!);
}
