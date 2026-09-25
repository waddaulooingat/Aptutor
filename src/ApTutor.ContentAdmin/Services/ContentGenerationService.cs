using System.Collections.Concurrent;
using ApTutor.Content;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;
using Microsoft.Extensions.Options;

namespace ApTutor.ContentAdmin.Services;

public enum GenerationStatus { Running, Succeeded, Failed }

// AiGenerated/AiReviewNote (see the AI-content-agent handoff) are additive to the original shape.
// AiGenerated records whether AutonomousContentAgentService triggered this job (vs. a person
// clicking Generate) regardless of how it's eventually reviewed. AiReviewNote is set only when the
// interim AI review pass ran and did NOT auto-approve — it carries the AI's flag reasoning so the
// human who eventually reviews this pending draft sees why the AI didn't trust it, same spirit as
// RejectionLog recording why a human discarded something, just surfaced inline instead of logged.
// A job the AI reviewer DID auto-approve never sits here at all — see ContentGenerationService's
// RunAsync remarks for why it's removed from this dictionary entirely, same end state as a human's
// own Approve click.
public sealed record GenerationJob(
    string CourseId, string NodeId, Difficulty Difficulty, bool AllowGraphChoices, GenerationStatus Status, string? Error, NodeContentPack? Pack, DateTimeOffset StartedAt,
    bool AiGenerated = false, string? AiReviewNote = null);

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
    private readonly AiReviewer _aiReviewer;
    private readonly S3ContentStore _store;
    private readonly IOptions<AiContentAgentOptions> _agentOptions;
    private readonly ILogger<ContentGenerationService> _logger;

    public ContentGenerationService(
        Generator generator, CourseCatalog catalog, AiReviewer aiReviewer, S3ContentStore store,
        IOptions<AiContentAgentOptions> agentOptions, ILogger<ContentGenerationService> logger)
    {
        _generator = generator;
        _catalog = catalog;
        _aiReviewer = aiReviewer;
        _store = store;
        _agentOptions = agentOptions;
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
    /// separately-billed API call. aiGenerated is true only when AutonomousContentAgentService's scan
    /// triggered this (see the AI-content-agent handoff) — every existing caller (a person clicking
    /// Generate) omits it and gets the same behavior as before.
    public bool TryStartGeneration(string courseId, string nodeId, Difficulty difficulty, bool allowGraphChoices, bool aiGenerated = false)
    {
        var key = Key(courseId, nodeId, difficulty);
        var startedAt = DateTimeOffset.UtcNow;
        var job = new GenerationJob(courseId, nodeId, difficulty, allowGraphChoices, GenerationStatus.Running, Error: null, Pack: null, startedAt, aiGenerated);
        if (!_jobs.TryAdd(key, job)) return false;

        var cts = new CancellationTokenSource();
        _cancellations[key] = cts;
        _ = RunAsync(courseId, nodeId, difficulty, allowGraphChoices, aiGenerated, key, startedAt, cts.Token);
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

    private async Task RunAsync(string courseId, string nodeId, Difficulty difficulty, bool allowGraphChoices, bool aiGenerated, string key, DateTimeOffset startedAt, CancellationToken ct)
    {
        try
        {
            var course = _catalog.Get(courseId);
            // Null-forgiving: reachable only via TryStartGeneration, which Review.cshtml.cs only
            // calls after TryLoadContext has already confirmed this exact course/node has a
            // non-null Graph — see its own null check.
            var node = course.Graph!.Node(nodeId);
            var pack = await _generator.GenerateAsync(courseId, node, difficulty, allowGraphChoices, ct);
            if (aiGenerated) pack = pack with { AiGenerated = true };

            // The AI review pass (see the AI-content-agent handoff) is an explicit, disabled-by-
            // default opt-in — see AiContentAgentOptions.Enabled's remarks. Off, this job behaves
            // exactly as it always has: sits Succeeded, pending a human's Approve/Reject.
            if (!_agentOptions.Value.Enabled)
            {
                TryCompleteJob(key, startedAt, job => job with { Status = GenerationStatus.Succeeded, Pack = pack });
                return;
            }

            await ReviewAndResolveAsync(courseId, node.Id, difficulty, node, pack, aiGenerated, key, startedAt, ct);
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

    /// The interim AI review pass — runs uniformly on every generation completion once
    /// AiContentAgentOptions.Enabled is true, regardless of whether a person or the autonomous scan
    /// triggered generation (see the AI-content-agent handoff's Part B: "whether triggered by the
    /// scan above or manually as today"). An Approve verdict goes live immediately, tagged
    /// AiReviewed so it's never indistinguishable from human-verified content; a Flag verdict (or a
    /// failure in the review call itself) leaves the draft as an ordinary pending job, exactly like
    /// today's ungated flow, with the AI's reasoning attached so the human who eventually looks at it
    /// sees why it wasn't auto-approved.
    private async Task ReviewAndResolveAsync(
        string courseId, string nodeId, Difficulty difficulty, DagNode node, NodeContentPack pack, bool aiGenerated,
        string key, DateTimeOffset startedAt, CancellationToken ct)
    {
        AiReviewResult review;
        try
        {
            review = await _aiReviewer.ReviewNodeContentAsync(node, difficulty, pack, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI review failed for {Course}/{Node}; leaving as a pending draft for manual review", courseId, nodeId);
            TryCompleteJob(key, startedAt, job => job with
            {
                Status = GenerationStatus.Succeeded, Pack = pack, AiGenerated = aiGenerated,
                AiReviewNote = "The AI review pass itself failed, so this needs manual review.",
            });
            return;
        }

        if (review.Verdict == AiReviewVerdict.Flag)
        {
            TryCompleteJob(key, startedAt, job => job with
            {
                Status = GenerationStatus.Succeeded, Pack = pack, AiGenerated = aiGenerated, AiReviewNote = review.Reasoning,
            });
            return;
        }

        var approvedPack = pack with { Verified = true, AiReviewed = true };
        try
        {
            await _store.ApproveNodeAsync(courseId, nodeId, approvedPack, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI auto-approve failed for {Course}/{Node}; leaving as a pending draft for manual approval", courseId, nodeId);
            TryCompleteJob(key, startedAt, job => job with
            {
                Status = GenerationStatus.Succeeded, Pack = pack, AiGenerated = aiGenerated,
                AiReviewNote = $"The AI reviewer approved this, but saving it failed ({ex.Message}) — please approve it manually.",
            });
            return;
        }

        // Live now — remove the job slot entirely rather than leaving a "Succeeded" entry with
        // Approve/Reject buttons for content that's already approved; same end state a human's own
        // Approve click leaves behind (see Review.cshtml.cs's OnPostApproveAsync).
        TryCompleteJob(key, startedAt, job => job with { Status = GenerationStatus.Succeeded, Pack = approvedPack, AiGenerated = aiGenerated });
        ClearJob(courseId, nodeId, difficulty, GenerationStatus.Succeeded);
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
