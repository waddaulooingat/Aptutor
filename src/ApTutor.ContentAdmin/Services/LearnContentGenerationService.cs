using System.Collections.Concurrent;
using ApTutor.Content;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;
using Microsoft.Extensions.Options;

namespace ApTutor.ContentAdmin.Services;

// AiGenerated/AiReviewNote mirror GenerationJob's own same-named fields — see its remarks for the
// full reasoning (AI Content Agent interim stopgap).
public sealed record LearnContentJob(
    string CourseId, string NodeId, GenerationStatus Status, string? Error, LearnContent? Content, DateTimeOffset StartedAt,
    bool AiGenerated = false, string? AiReviewNote = null);

/// Same shape as ContentGenerationService (background job + polling page instead of blocking the
/// HTTP request for the 10-30+ second Claude call), kept as its own near-identical class rather than
/// a generic "job tracker of T" — this project's own established pattern (see
/// UnitStructureGenerationService/CourseCreationService) — because the two content types differ in
/// how they're keyed: this one has no difficulty axis (see the learn-quiz-mode-switch plan's Part B
/// and S3ContentStore.ApproveLearnContentAsync's remarks), so trying to force a shared abstraction
/// with ContentGenerationService would just reintroduce a difficulty parameter this content type
/// doesn't have.
public sealed class LearnContentGenerationService
{
    private readonly ConcurrentDictionary<string, LearnContentJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly Generator _generator;
    private readonly CourseCatalog _catalog;
    private readonly AiReviewer _aiReviewer;
    private readonly S3ContentStore _store;
    private readonly IOptions<AiContentAgentOptions> _agentOptions;
    private readonly ILogger<LearnContentGenerationService> _logger;

    public LearnContentGenerationService(
        Generator generator, CourseCatalog catalog, AiReviewer aiReviewer, S3ContentStore store,
        IOptions<AiContentAgentOptions> agentOptions, ILogger<LearnContentGenerationService> logger)
    {
        _generator = generator;
        _catalog = catalog;
        _aiReviewer = aiReviewer;
        _store = store;
        _agentOptions = agentOptions;
        _logger = logger;
    }

    private static string Key(string courseId, string nodeId) => $"{courseId}/{nodeId}";

    public LearnContentJob? GetJob(string courseId, string nodeId) =>
        _jobs.TryGetValue(Key(courseId, nodeId), out var job) ? job : null;

    public bool TryStartGeneration(string courseId, string nodeId)
    {
        var key = Key(courseId, nodeId);
        var startedAt = DateTimeOffset.UtcNow;
        var job = new LearnContentJob(courseId, nodeId, GenerationStatus.Running, Error: null, Content: null, startedAt);
        if (!_jobs.TryAdd(key, job)) return false;

        var cts = new CancellationTokenSource();
        _cancellations[key] = cts;
        _ = RunAsync(courseId, nodeId, key, startedAt, cts.Token);
        return true;
    }

    public void RestoreJob(LearnContentJob job) => _jobs.TryAdd(Key(job.CourseId, job.NodeId), job);

    public bool ClearJob(string courseId, string nodeId, GenerationStatus expectedStatus)
    {
        var key = Key(courseId, nodeId);
        if (!_jobs.TryGetValue(key, out var job) || job.Status != expectedStatus) return false;

        var removed = ((ICollection<KeyValuePair<string, LearnContentJob>>)_jobs)
            .Remove(new KeyValuePair<string, LearnContentJob>(key, job));

        if (removed && _cancellations.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        return removed;
    }

    private async Task RunAsync(string courseId, string nodeId, string key, DateTimeOffset startedAt, CancellationToken ct)
    {
        try
        {
            var course = _catalog.Get(courseId);
            var node = course.Graph!.Node(nodeId);
            var content = await _generator.GenerateLearnContentAsync(courseId, node, ct);

            if (!_agentOptions.Value.Enabled)
            {
                TryCompleteJob(key, startedAt, job => job with { Status = GenerationStatus.Succeeded, Content = content });
                return;
            }

            await ReviewAndResolveAsync(courseId, nodeId, node, content, key, startedAt, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Learn-content generation failed for {Course}/{Node}", courseId, nodeId);
            TryCompleteJob(key, startedAt, job => job with { Status = GenerationStatus.Failed, Error = ex.Message });
        }
    }

    /// Same interim AI review pass as ContentGenerationService — see its ReviewAndResolveAsync for
    /// the full reasoning; this is the learn-content counterpart.
    private async Task ReviewAndResolveAsync(
        string courseId, string nodeId, DagNode node, LearnContent content, string key, DateTimeOffset startedAt, CancellationToken ct)
    {
        AiReviewResult review;
        try
        {
            review = await _aiReviewer.ReviewLearnContentAsync(node, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI review failed for {Course}/{Node} learn content; leaving as a pending draft for manual review", courseId, nodeId);
            TryCompleteJob(key, startedAt, job => job with
            {
                Status = GenerationStatus.Succeeded, Content = content,
                AiReviewNote = "The AI review pass itself failed, so this needs manual review.",
            });
            return;
        }

        if (review.Verdict == AiReviewVerdict.Flag)
        {
            TryCompleteJob(key, startedAt, job => job with { Status = GenerationStatus.Succeeded, Content = content, AiReviewNote = review.Reasoning });
            return;
        }

        var approved = content with { Verified = true, AiReviewed = true };
        try
        {
            await _store.ApproveLearnContentAsync(courseId, nodeId, approved, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI auto-approve failed for {Course}/{Node} learn content; leaving as a pending draft for manual approval", courseId, nodeId);
            TryCompleteJob(key, startedAt, job => job with
            {
                Status = GenerationStatus.Succeeded, Content = content,
                AiReviewNote = $"The AI reviewer approved this, but saving it failed ({ex.Message}) — please approve it manually.",
            });
            return;
        }

        TryCompleteJob(key, startedAt, job => job with { Status = GenerationStatus.Succeeded, Content = approved });
        ClearJob(courseId, nodeId, GenerationStatus.Succeeded);
    }

    private void TryCompleteJob(string key, DateTimeOffset startedAt, Func<LearnContentJob, LearnContentJob> update)
    {
        if (_jobs.TryGetValue(key, out var current) && current.StartedAt == startedAt)
            _jobs.TryUpdate(key, update(current), current);
    }
}
