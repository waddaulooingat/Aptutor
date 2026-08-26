using ApTutor.Curriculum;

namespace ApTutor.ContentAdmin.Services;

/// Graph is null for a course that exists (it's in the top-level S3 index) but has no approved
/// structure yet — e.g. a course just created via "Create new course" (unit list approved, no DAG
/// filled in per unit yet). Callers must handle that rather than assuming every registered course
/// has node/unit data ready to render.
public sealed record CourseInfo(string CourseId, string DisplayName, SkillGraph? Graph);

/// Discovers courses from S3 (the top-level manifest's course index) instead of a static
/// appsettings.json list — course structure is itself SME-approved, generator-produced material now
/// (see the Shell-display-only/course-authoring plan), not curriculum code baked into the build.
public sealed class CourseCatalog
{
    private readonly S3ContentStore _store;
    private readonly ILogger<CourseCatalog> _logger;
    private IReadOnlyDictionary<string, CourseInfo> _courses = new Dictionary<string, CourseInfo>(StringComparer.Ordinal);

    public CourseCatalog(S3ContentStore store, ILogger<CourseCatalog> logger)
    {
        _store = store;
        _logger = logger;
    }

    public IReadOnlyCollection<CourseInfo> All => _courses.Values.ToList();

    public CourseInfo Get(string courseId) =>
        _courses.TryGetValue(courseId, out var course)
            ? course
            : throw new KeyNotFoundException($"Unknown course '{courseId}'.");

    public bool TryGet(string courseId, out CourseInfo course) =>
        _courses.TryGetValue(courseId, out course!);

    /// Re-discovers every course from S3. Called once at startup (see Program.cs); call again after
    /// any authoring action that could change the course list or a course's structure (a new course
    /// created, a unit's structure regenerated) so this app's own view of the world doesn't go stale
    /// mid-session without a restart.
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var topLevel = await _store.GetTopLevelManifestAsync(ct);
        var courses = new Dictionary<string, CourseInfo>(StringComparer.Ordinal);

        foreach (var courseId in topLevel.Courses.Keys)
        {
            var structure = await _store.TryGetLiveStructureAsync(courseId, ct);
            var graph = structure is not null ? new SkillGraph(structure) : null;
            // DisplayName, never Course — Course can be a non-compliant descriptive string (e.g.
            // "AP World History: Modern"); see DagMeta's remarks.
            var displayName = structure?.Meta.DisplayName ?? structure?.Meta.Course ?? courseId;
            courses[courseId] = new CourseInfo(courseId, displayName, graph);
        }

        _courses = courses;
        _logger.LogInformation("Course catalog refreshed from S3: {Count} course(s).", courses.Count);
    }
}
