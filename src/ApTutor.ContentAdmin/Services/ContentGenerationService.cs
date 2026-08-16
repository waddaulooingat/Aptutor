using System.Collections.Concurrent;
using ApTutor.Content;
using ApTutor.ContentFactory;

namespace ApTutor.ContentAdmin.Services;

public enum GenerationStatus { Running, Succeeded, Failed }

public sealed record GenerationJob(string CourseId, string NodeId, GenerationStatus Status, string? Error, DateTimeOffset StartedAt);

/// Wraps ApTutor.ContentFactory's Generator so a "Generate" click returns immediately (redirect to
/// an auto-refreshing polling page) instead of blocking the HTTP request for the 10-30+ seconds a
/// real Claude call can take — avoids a reverse-proxy timeout mid-request, and doubles as the
/// per-node "generation is already in flight" guard the SME's Generate button needs (one job per
/// node at a time; TryStartGeneration refuses a second job for a node that's still running).
public sealed class ContentGenerationService
{
    private readonly ConcurrentDictionary<string, GenerationJob> _jobs = new(StringComparer.Ordinal);
    private readonly Generator _generator;
    private readonly CourseCatalog _catalog;
    private readonly ILogger<ContentGenerationService> _logger;

    public ContentGenerationService(Generator generator, CourseCatalog catalog, ILogger<ContentGenerationService> logger)
    {
        _generator = generator;
        _catalog = catalog;
        _logger = logger;
    }

    private static string Key(string courseId, string nodeId) => $"{courseId}/{nodeId}";

    public GenerationJob? GetJob(string courseId, string nodeId) =>
        _jobs.TryGetValue(Key(courseId, nodeId), out var job) ? job : null;

    /// Starts a background generation for this node if none is already running for it. Returns
    /// false — and starts nothing — if a job for this exact node is already in flight, so the page
    /// handler can just redirect to the existing job's polling page instead of firing a duplicate,
    /// separately-billed API call.
    public bool TryStartGeneration(string courseId, string nodeId)
    {
        var key = Key(courseId, nodeId);
        var job = new GenerationJob(courseId, nodeId, GenerationStatus.Running, Error: null, DateTimeOffset.UtcNow);
        if (!_jobs.TryAdd(key, job)) return false;

        _ = RunAsync(courseId, nodeId, key);
        return true;
    }

    /// Drops a completed job's tracking entry (called once the SME has navigated past the polling
    /// page) so the dictionary doesn't grow forever and the node can be regenerated later.
    public void ClearJob(string courseId, string nodeId) => _jobs.TryRemove(Key(courseId, nodeId), out _);

    private async Task RunAsync(string courseId, string nodeId, string key)
    {
        try
        {
            var course = _catalog.Get(courseId);
            var node = course.Graph.Node(nodeId);
            var pack = await _generator.GenerateAsync(courseId, node);
            ContentPackStore.Save(course.ContentDir, pack);
            _jobs[key] = _jobs[key] with { Status = GenerationStatus.Succeeded };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generation failed for {Course}/{Node}", courseId, nodeId);
            _jobs[key] = _jobs[key] with { Status = GenerationStatus.Failed, Error = ex.Message };
        }
    }
}
