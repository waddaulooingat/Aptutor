using System.Collections.Concurrent;
using ApTutor.Content;
using ApTutor.ContentFactory;

namespace ApTutor.ContentAdmin.Services;

public enum GenerationStatus { Running, Succeeded, Failed }

public sealed record GenerationJob(
    string CourseId, string NodeId, Difficulty Difficulty, GenerationStatus Status, string? Error, NodeContentPack? Pack, DateTimeOffset StartedAt);

/// Wraps ApTutor.ContentFactory's Generator so a "Generate" click returns immediately (redirect to
/// an auto-refreshing polling page) instead of blocking the HTTP request for the 10-30+ seconds a
/// real Claude call can take — avoids a reverse-proxy timeout mid-request, and doubles as the
/// per-node "generation is already in flight" guard the SME's Generate button needs (one job per
/// node at a time; TryStartGeneration refuses a second job for a node that's still running).
///
/// A successful generation's draft lives ONLY here, in memory, until Approve — nothing is written
/// to S3 until then (see S3ContentStore/RejectionLog). A redeploy mid-review loses an unapproved
/// draft; the SME just regenerates. Single-instance deployment is assumed for this reason — see
/// the plan.
public sealed class ContentGenerationService
{
    private readonly ConcurrentDictionary<string, GenerationJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly Generator _generator;
    private readonly CourseCatalog _catalog;
    private readonly ILogger<ContentGenerationService> _logger;

    public ContentGenerationService(Generator generator, CourseCatalog catalog, ILogger<ContentGenerationService> logger)
    {
        _generator = generator;
        _catalog = catalog;
        _logger = logger;
    }

    // Keyed by (course, node, difficulty) — not just (course, node) — so generating "hard" for a
    // node doesn't block or collide with generating "easy" for the same node; each difficulty is an
    // independent job slot, same as a node can accumulate independently-approved sets per difficulty
    // (see the difficulty-levels plan).
    private static string Key(string courseId, string nodeId, Difficulty difficulty) => $"{courseId}/{nodeId}/{difficulty}";

    public GenerationJob? GetJob(string courseId, string nodeId, Difficulty difficulty) =>
        _jobs.TryGetValue(Key(courseId, nodeId, difficulty), out var job) ? job : null;

    /// Starts a background generation for this node+difficulty if none is already running for it.
    /// Returns false — and starts nothing — if a job for this exact node+difficulty is already
    /// tracked (running, succeeded-but-not-yet-approved, or failed-but-not-yet-cleared), so the page
    /// handler can just redirect to the existing job's polling page instead of firing a duplicate,
    /// separately-billed API call.
    public bool TryStartGeneration(string courseId, string nodeId, Difficulty difficulty)
    {
        var key = Key(courseId, nodeId, difficulty);
        var startedAt = DateTimeOffset.UtcNow;
        var job = new GenerationJob(courseId, nodeId, difficulty, GenerationStatus.Running, Error: null, Pack: null, startedAt);
        if (!_jobs.TryAdd(key, job)) return false;

        var cts = new CancellationTokenSource();
        _cancellations[key] = cts;
        _ = RunAsync(courseId, nodeId, difficulty, key, startedAt, cts.Token);
        return true;
    }

    /// Best-effort: re-inserts a job that was already claimed via ClearJob (e.g. because a
    /// downstream S3 write then failed), so the SME can retry instead of losing the draft. No-ops
    /// if a newer job already occupies the slot — a fresh TryStartGeneration beat this restore.
    public void RestoreJob(GenerationJob job) => _jobs.TryAdd(Key(job.CourseId, job.NodeId, job.Difficulty), job);

    /// Atomically claims and removes a job, but only if its current status matches
    /// <paramref name="expectedStatus"/> — a compare-and-remove, not a blind delete. Two
    /// near-simultaneous Approve/Reject/polling-page requests can both observe the same
    /// "Succeeded"/"Failed" snapshot; only one of them will actually win this call, so the loser
    /// can treat the job as already handled instead of double-acting on it. Returns false if the
    /// job doesn't exist or was already claimed by someone else.
    public bool ClearJob(string courseId, string nodeId, Difficulty difficulty, GenerationStatus expectedStatus)
    {
        var key = Key(courseId, nodeId, difficulty);
        if (!_jobs.TryGetValue(key, out var job) || job.Status != expectedStatus) return false;

        var removed = ((ICollection<KeyValuePair<string, GenerationJob>>)_jobs)
            .Remove(new KeyValuePair<string, GenerationJob>(key, job));

        if (removed && _cancellations.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        return removed;
    }

    private async Task RunAsync(string courseId, string nodeId, Difficulty difficulty, string key, DateTimeOffset startedAt, CancellationToken ct)
    {
        try
        {
            var course = _catalog.Get(courseId);
            // Null-forgiving: reachable only via TryStartGeneration, which Review.cshtml.cs only
            // calls after TryLoadContext has already confirmed this exact course/node has a
            // non-null Graph — see its own null check.
            var node = course.Graph!.Node(nodeId);
            var pack = await _generator.GenerateAsync(courseId, node, difficulty, ct);
            TryCompleteJob(key, startedAt, job => job with { Status = GenerationStatus.Succeeded, Pack = pack });
        }
        catch (OperationCanceledException)
        {
            // The job was rejected/cleared while generation was still in flight — don't resurrect it.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generation failed for {Course}/{Node}", courseId, nodeId);
            TryCompleteJob(key, startedAt, job => job with { Status = GenerationStatus.Failed, Error = ex.Message });
        }
    }

    /// Only writes the result back if this key still points at the exact job we started (same
    /// StartedAt) — if it was cleared (Reject, or the polling page's own cleanup) and possibly
    /// replaced by a newer generation attempt in the meantime, this quietly no-ops instead of
    /// clobbering whatever's there now.
    private void TryCompleteJob(string key, DateTimeOffset startedAt, Func<GenerationJob, GenerationJob> update)
    {
        if (_jobs.TryGetValue(key, out var current) && current.StartedAt == startedAt)
            _jobs.TryUpdate(key, update(current), current); // no-ops if the entry moved on since the read above
    }
}
