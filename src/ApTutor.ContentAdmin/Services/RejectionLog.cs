using System.Text.Json;

namespace ApTutor.ContentAdmin.Services;

public sealed record RejectionEntry(string CourseId, string NodeId, string Reason, DateTimeOffset RejectedAt);

/// Records that a rejection happened, durably — separately from the rejected draft itself. The
/// *generated draft* is deleted and never enters git history (matches "no trace of a rejected
/// attempt", the SME's mental model has no git terms in it at all). But the *fact* that a
/// rejection happened, and why, has to survive a redeploy so it can inform prompt improvements
/// later, and local-disk-only doesn't guarantee that on most hosts — so this is committed and
/// pushed separately, in its own small commit, via the same GitRepoService approvals use.
public sealed class RejectionLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly GitRepoService _git;

    public RejectionLog(GitRepoService git) => _git = git;

    public async Task RecordAsync(CourseInfo course, string nodeId, string reason, CancellationToken ct = default)
    {
        var entry = new RejectionEntry(course.CourseId, nodeId, reason, DateTimeOffset.UtcNow);
        var absolutePath = Path.Combine(course.ContentDir, ".rejections.jsonl");

        Directory.CreateDirectory(course.ContentDir);
        await File.AppendAllTextAsync(absolutePath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, ct);

        var relativePath = Path.GetRelativePath(_git.CloneDir, absolutePath).Replace('\\', '/');
        await _git.CommitAndPushAsync(new[] { relativePath }, $"content-admin: log rejection of {course.CourseId}/{nodeId}", ct);
    }
}
