using System.Collections.Concurrent;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;

namespace ApTutor.ContentAdmin.Services;

public enum CourseCreationStatus { Running, Succeeded, Failed }

public sealed record CourseCreationJob(
    string CourseId, string CourseName, string DisplayName, CourseCreationStatus Status, string? Error,
    IReadOnlyList<UnitInfo>? ProposedUnits, DateTimeOffset StartedAt);

/// Content Admin's "Create new course" (see the Shell-display-only/course-authoring plan's Part C)
/// — the top-level authoring entry point, one level above UnitStructureGenerationService: drafts
/// only a new course's unit list (its table of contents), never any unit's node structure. Same
/// Generate-returns-immediately/poll/Approve job-tracking shape as ContentGenerationService and
/// UnitStructureGenerationService — see their remarks for why.
///
/// No CourseCatalog dependency, unlike UnitStructureGenerationService: a brand-new course being
/// created here has no existing structure to give the model as prereq context, so there's nothing
/// to look up.
public sealed class CourseCreationService
{
    private readonly ConcurrentDictionary<string, CourseCreationJob> _jobs = new(StringComparer.Ordinal);
    private readonly Generator _generator;
    private readonly ILogger<CourseCreationService> _logger;

    public CourseCreationService(Generator generator, ILogger<CourseCreationService> logger)
    {
        _generator = generator;
        _logger = logger;
    }

    public CourseCreationJob? GetJob(string courseId) =>
        _jobs.TryGetValue(courseId, out var job) ? job : null;

    /// Starts a background generation for this courseId if none is already running for it. Returns
    /// false — and starts nothing — if a job is already tracked.
    public bool TryStartGeneration(string courseId, string courseName, string displayName, string? guidance)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var job = new CourseCreationJob(courseId, courseName, displayName, CourseCreationStatus.Running, Error: null, ProposedUnits: null, startedAt);
        if (!_jobs.TryAdd(courseId, job)) return false;

        _ = RunAsync(courseId, courseName, guidance, startedAt);
        return true;
    }

    /// Best-effort: re-inserts a job — typically with ProposedUnits swapped for the SME's edited/
    /// kept set — after it was already claimed via ClearJob but a downstream S3 write then failed.
    /// No-ops if a newer job already occupies the slot.
    public void RestoreJob(CourseCreationJob job) => _jobs.TryAdd(job.CourseId, job);

    /// Atomically claims and removes a job, but only if its current status matches
    /// expectedStatus — same compare-and-remove reasoning as the other generation services' ClearJob.
    public bool ClearJob(string courseId, CourseCreationStatus expectedStatus)
    {
        if (!_jobs.TryGetValue(courseId, out var job) || job.Status != expectedStatus) return false;

        return ((ICollection<KeyValuePair<string, CourseCreationJob>>)_jobs)
            .Remove(new KeyValuePair<string, CourseCreationJob>(courseId, job));
    }

    private async Task RunAsync(string courseId, string courseName, string? guidance, DateTimeOffset startedAt)
    {
        try
        {
            var proposed = await _generator.GenerateCourseUnitListAsync(courseId, courseName, guidance);
            TryCompleteJob(courseId, startedAt, job => job with { Status = CourseCreationStatus.Succeeded, ProposedUnits = proposed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Course creation failed for {CourseId}", courseId);
            TryCompleteJob(courseId, startedAt, job => job with { Status = CourseCreationStatus.Failed, Error = ex.Message });
        }
    }

    private void TryCompleteJob(string courseId, DateTimeOffset startedAt, Func<CourseCreationJob, CourseCreationJob> update)
    {
        if (_jobs.TryGetValue(courseId, out var current) && current.StartedAt == startedAt)
            _jobs.TryUpdate(courseId, update(current), current);
    }
}
