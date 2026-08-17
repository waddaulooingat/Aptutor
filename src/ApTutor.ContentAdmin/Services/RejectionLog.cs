using System.Text.Json;

namespace ApTutor.ContentAdmin.Services;

/// ItemId/ItemPrompt are null for a whole-node rejection; set when a single practice item was
/// discarded during an otherwise-successful Approve.
public sealed record RejectionEntry(
    string CourseId, string NodeId, string? ItemId, string? ItemPrompt, string Reason, DateTimeOffset RejectedAt);

/// Records that a rejection happened, durably — separately from the rejected content itself. The
/// *generated draft* (whole-node reject) or the *discarded item* (per-item discard during Approve)
/// is deleted and never enters git history (matches "no trace of a rejected attempt" — the SME's
/// mental model has no git terms in it at all). But the *fact* that a rejection happened, and why,
/// has to survive a redeploy so it can inform prompt improvements later, and local-disk-only
/// doesn't guarantee that on most hosts — so this is committed and pushed separately, in its own
/// small commit, via the same GitRepoService approvals use.
public sealed class RejectionLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly GitRepoService _git;

    public RejectionLog(GitRepoService git) => _git = git;

    /// Whole-node rejection (Review's "Reject the whole topic" action): the entire generated draft
    /// is discarded.
    public Task RecordAsync(CourseInfo course, string nodeId, string reason, CancellationToken ct = default) =>
        RecordManyAsync(course, new[] { new RejectionEntry(course.CourseId, nodeId, null, null, reason, DateTimeOffset.UtcNow) }, ct);

    /// One or more individual practice items discarded during an otherwise-successful Approve.
    /// Written and pushed together in one commit rather than one commit per item, so keeping 2
    /// good questions and discarding 1 bad one doesn't produce 2 separate pushes.
    public Task RecordItemsAsync(
        CourseInfo course, string nodeId, IReadOnlyList<(string ItemId, string ItemPrompt, string Reason)> items, CancellationToken ct = default) =>
        RecordManyAsync(
            course,
            items.Select(i => new RejectionEntry(course.CourseId, nodeId, i.ItemId, i.ItemPrompt, i.Reason, DateTimeOffset.UtcNow)).ToList(),
            ct);

    private async Task RecordManyAsync(CourseInfo course, IReadOnlyList<RejectionEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;

        var absolutePath = Path.Combine(course.ContentDir, ".rejections.jsonl");
        Directory.CreateDirectory(course.ContentDir);
        var lines = entries.Select(e => JsonSerializer.Serialize(e, JsonOptions));
        await File.AppendAllLinesAsync(absolutePath, lines, ct);

        var relativePath = Path.GetRelativePath(_git.CloneDir, absolutePath).Replace('\\', '/');
        var nodeId = entries[0].NodeId;
        var message = entries.Count == 1 && entries[0].ItemId is null
            ? $"content-admin: log rejection of {course.CourseId}/{nodeId}"
            : $"content-admin: log {entries.Count} discarded item(s) for {course.CourseId}/{nodeId}";
        await _git.CommitAndPushAsync(new[] { relativePath }, message, ct);
    }
}
