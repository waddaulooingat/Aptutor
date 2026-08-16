using ApTutor.Curriculum;

namespace ApTutor.ContentAdmin.Services;

public sealed record CourseInfo(string CourseId, string DisplayName, SkillGraph Graph, string ContentDir);

/// Loads each configured course's skill DAG once, resolved against GitRepoService's actual on-disk
/// clone (ContentAdmin:RepoCloneDir + each course's DagPath/ContentDir from appsettings) — the same
/// DAG the desktop app's course modules load, just pointed at whatever this process currently has
/// checked out. Registered after Program.cs has already awaited GitRepoService.EnsureUpToDateAsync
/// at startup, so the DAG/content files are guaranteed to exist by the time this constructs.
public sealed class CourseCatalog
{
    private readonly Dictionary<string, CourseInfo> _courses;

    public CourseCatalog(IConfiguration config, GitRepoService git)
    {
        _courses = new Dictionary<string, CourseInfo>(StringComparer.Ordinal);

        foreach (var courseSection in config.GetSection("ContentAdmin:Courses").GetChildren())
        {
            var courseId = courseSection.Key;
            var displayName = courseSection["DisplayName"] ?? courseId;
            var dagRelativePath = courseSection["DagPath"]
                ?? throw new InvalidOperationException($"Course '{courseId}' is missing DagPath in configuration.");
            var contentRelativeDir = courseSection["ContentDir"]
                ?? throw new InvalidOperationException($"Course '{courseId}' is missing ContentDir in configuration.");

            var dagPath = Path.Combine(git.CloneDir, dagRelativePath);
            var contentDir = Path.Combine(git.CloneDir, contentRelativeDir);
            var graph = SkillDagLoader.Load(dagPath);

            _courses[courseId] = new CourseInfo(courseId, displayName, graph, contentDir);
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
