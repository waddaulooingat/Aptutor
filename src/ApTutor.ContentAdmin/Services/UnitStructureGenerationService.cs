using System.Collections.Concurrent;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;

namespace ApTutor.ContentAdmin.Services;

public enum StructureGenerationStatus { Running, Succeeded, Failed }

public sealed record UnitStructureJob(
    string CourseId, int Unit, string UnitTitle, StructureGenerationStatus Status, string? Error,
    IReadOnlyList<DagNode>? ProposedNodes, DateTimeOffset StartedAt);

/// Content Admin's "Generate unit structure" (see the Shell-display-only/course-authoring plan's
/// Part B) — one level above ContentGenerationService's per-node content generation, same shape for
/// the same reason: a "Generate" click returns immediately (redirect to a polling page) instead of
/// blocking the request for the 10-30+ second Claude call, and TryStartGeneration doubles as the
/// per-(course,unit) "already in flight" guard.
///
/// A successful draft lives only here, in memory, until Approve — nothing is written to S3 until
/// then. A redeploy mid-review loses an unapproved draft; the SME just regenerates. No
/// RestoreJob/cancellation-token machinery here unlike ContentGenerationService: there's no
/// per-item Reject flow to race against, just Approve-or-discard-the-whole-draft.
public sealed class UnitStructureGenerationService
{
    private readonly ConcurrentDictionary<string, UnitStructureJob> _jobs = new(StringComparer.Ordinal);
    private readonly Generator _generator;
    private readonly CourseCatalog _catalog;
    private readonly ILogger<UnitStructureGenerationService> _logger;

    public UnitStructureGenerationService(Generator generator, CourseCatalog catalog, ILogger<UnitStructureGenerationService> logger)
    {
        _generator = generator;
        _catalog = catalog;
        _logger = logger;
    }

    private static string Key(string courseId, int unit) => $"{courseId}/{unit}";

    public UnitStructureJob? GetJob(string courseId, int unit) =>
        _jobs.TryGetValue(Key(courseId, unit), out var job) ? job : null;

    /// Starts a background generation for this (course, unit) if none is already running for it.
    /// Returns false — and starts nothing — if a job is already tracked (running, succeeded-but-
    /// not-yet-approved, or failed-but-not-yet-cleared).
    public bool TryStartGeneration(string courseId, int unit, string unitTitle, string? guidance)
    {
        var key = Key(courseId, unit);
        var startedAt = DateTimeOffset.UtcNow;
        var job = new UnitStructureJob(courseId, unit, unitTitle, StructureGenerationStatus.Running, Error: null, ProposedNodes: null, startedAt);
        if (!_jobs.TryAdd(key, job)) return false;

        _ = RunAsync(courseId, unit, unitTitle, guidance, key, startedAt);
        return true;
    }

    /// Best-effort: re-inserts a job — typically with ProposedNodes swapped for the SME's edited/
    /// kept set — after it was already claimed via ClearJob but a downstream S3 write then failed,
    /// so their edits aren't lost and they can retry instead of regenerating from scratch. No-ops if
    /// a newer job already occupies the slot.
    public void RestoreJob(UnitStructureJob job) => _jobs.TryAdd(Key(job.CourseId, job.Unit), job);

    /// Atomically claims and removes a job, but only if its current status matches
    /// expectedStatus — same compare-and-remove reasoning as ContentGenerationService.ClearJob.
    public bool ClearJob(string courseId, int unit, StructureGenerationStatus expectedStatus)
    {
        var key = Key(courseId, unit);
        if (!_jobs.TryGetValue(key, out var job) || job.Status != expectedStatus) return false;

        return ((ICollection<KeyValuePair<string, UnitStructureJob>>)_jobs)
            .Remove(new KeyValuePair<string, UnitStructureJob>(key, job));
    }

    private async Task RunAsync(string courseId, int unit, string unitTitle, string? guidance, string key, DateTimeOffset startedAt)
    {
        try
        {
            if (!_catalog.TryGet(courseId, out var courseInfo))
                throw new InvalidOperationException($"Unknown course '{courseId}'.");

            var existingNodes = courseInfo.Graph?.Dag.Nodes ?? Array.Empty<DagNode>();
            var proposed = await _generator.GenerateUnitStructureAsync(courseId, unit, unitTitle, existingNodes, guidance);
            TryCompleteJob(key, startedAt, job => job with { Status = StructureGenerationStatus.Succeeded, ProposedNodes = proposed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unit structure generation failed for {Course} unit {Unit}", courseId, unit);
            TryCompleteJob(key, startedAt, job => job with { Status = StructureGenerationStatus.Failed, Error = ex.Message });
        }
    }

    /// Only writes the result back if this key still points at the exact job we started (same
    /// StartedAt) — same guard as ContentGenerationService.TryCompleteJob, for the same reason.
    private void TryCompleteJob(string key, DateTimeOffset startedAt, Func<UnitStructureJob, UnitStructureJob> update)
    {
        if (_jobs.TryGetValue(key, out var current) && current.StartedAt == startedAt)
            _jobs.TryUpdate(key, update(current), current);
    }
}
