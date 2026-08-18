using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace ApTutor.ContentAdmin.Services;

/// ItemId/ItemPrompt are null for a whole-node rejection; set when a single practice item was
/// discarded during an otherwise-successful Approve.
public sealed record RejectedItem(string? ItemId, string? ItemPrompt, string Reason);
public sealed record RejectionEvent(string CourseId, string NodeId, IReadOnlyList<RejectedItem> Items, DateTimeOffset RejectedAt);

/// Records that a rejection happened, durably — separately from the rejected content itself. The
/// generated draft never touches S3 at all (it only ever lived in ContentGenerationService's
/// in-memory job dictionary — see that file), so there's nothing to delete here. But the *fact*
/// that a rejection happened, and why, needs to survive a redeploy so it can inform prompt
/// improvements later. One S3 object per rejection event (not an appended log) — avoids any
/// read-modify-write race on a shared file, since each event gets its own uniquely-keyed PUT.
public sealed class RejectionLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public RejectionLog(IAmazonS3 s3, IOptions<S3ContentStoreOptions> options)
    {
        _s3 = s3;
        _bucket = options.Value.Bucket;
    }

    /// Whole-node rejection (Review's "Reject the whole topic" action).
    public Task RecordAsync(string courseId, string nodeId, string reason, CancellationToken ct = default) =>
        RecordEventAsync(courseId, nodeId, new[] { new RejectedItem(null, null, reason) }, ct);

    /// One or more individual practice items discarded during an otherwise-successful Approve —
    /// written as a single event/object, not one PUT per item.
    public Task RecordItemsAsync(
        string courseId, string nodeId, IReadOnlyList<(string ItemId, string ItemPrompt, string Reason)> discardedItems, CancellationToken ct = default) =>
        RecordEventAsync(courseId, nodeId, discardedItems.Select(i => new RejectedItem(i.ItemId, i.ItemPrompt, i.Reason)).ToList(), ct);

    private async Task RecordEventAsync(string courseId, string nodeId, IReadOnlyList<RejectedItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;

        var rejectedAt = DateTimeOffset.UtcNow;
        var evt = new RejectionEvent(courseId, nodeId, items, rejectedAt);
        var key = $"courses/{courseId}/rejections/{nodeId}-{rejectedAt.UtcTicks}.json";

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentBody = JsonSerializer.Serialize(evt, JsonOptions),
            ContentType = "application/json",
        }, ct);
    }
}
