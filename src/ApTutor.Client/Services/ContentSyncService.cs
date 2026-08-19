using System.Net;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using ApTutor.Content;
using ApTutor.Platform;

namespace ApTutor.Client.Services;

public sealed record SyncResult(bool Success, IReadOnlyList<string> UpdatedNodeIds, string? UserMessage);

/// Minimal, temporary test bridge from S3 into the desktop Shell — NOT the shipping design. Pulls
/// approved content straight from S3 with a read-only credential; no Licensing/entitlement service
/// or signed URLs in front of it yet (that's the next pass). Refreshes only the given course, and
/// only ever adds/updates locally-cached nodes — never deletes anything, since this is a pull of
/// what's currently approved, not a mirror.
public class ContentSyncService
{
    private const string OfflineMessage =
        "You are offline. This app needs an internet connection. Til then you can review the previously downloaded content.";

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public ContentSyncService(IAmazonS3 s3, string bucket)
    {
        _s3 = s3;
        _bucket = bucket;
    }

    public async Task<SyncResult> RefreshAsync(ICourseModule course, CancellationToken ct = default)
    {
        try
        {
            var manifest = await GetManifestAsync(course.CourseId, ct);
            var toDownload = ContentSyncPlanner.ComputeNodesToDownload(
                manifest,
                nodeId => ContentPackStore.TryLoad(course.ContentDir, nodeId) is { } cached ? ContentHash.Compute(cached) : null);

            var actuallyUpdated = new List<string>();
            foreach (var node in toDownload)
            {
                // Content-addressed: the hash from the manifest diff IS the object's key, not just
                // a value to compare — see S3ContentStore's layout notes.
                var pack = await GetNodeAsync(course.CourseId, node.NodeId, node.Hash, ct);
                if (pack is not null)
                {
                    ContentPackStore.Save(course.ContentDir, pack);
                    actuallyUpdated.Add(node.NodeId);
                }
            }

            // Named explicitly, not just counted — so it's obvious during testing whether a given
            // node actually came from S3 this run versus was already cached from before.
            if (actuallyUpdated.Count > 0)
                Console.WriteLine($"[ContentSyncService] Pulled from S3 for '{course.CourseId}': {string.Join(", ", actuallyUpdated)}");
            else
                Console.WriteLine($"[ContentSyncService] '{course.CourseId}' already up to date — nothing pulled from S3.");

            return new SyncResult(true, actuallyUpdated, null);
        }
        catch (Exception ex)
        {
            // Never let a raw exception (network down, bad credentials, bucket typo, whatever the
            // real cause) reach the UI as-is — the student sees one plain, non-technical message;
            // the real cause goes to the console so this is still debuggable during testing.
            Console.Error.WriteLine($"[ContentSyncService] Refresh failed for course '{course.CourseId}': {ex}");
            return new SyncResult(false, Array.Empty<string>(), OfflineMessage);
        }
    }

    // The only two methods that actually touch IAmazonS3 — a narrow protected seam (same pattern as
    // ApTutor.ContentAdmin's S3ContentStore) so tests can exercise the real diff-and-download logic
    // above via a subclass override, without hand-stubbing the whole IAmazonS3 interface.
    protected virtual async Task<CourseManifestSnapshot> GetManifestAsync(string courseId, CancellationToken ct)
    {
        try
        {
            using var response = await _s3.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = $"courses/{courseId}/manifest.json" }, ct);
            using var reader = new StreamReader(response.ResponseStream);
            var body = await reader.ReadToEndAsync(ct);

            var manifest = JsonSerializer.Deserialize<S3CourseManifest>(body, ContentHash.CanonicalOptions);
            var nodes = (manifest?.Nodes ?? new Dictionary<string, S3NodeManifestEntry>())
                .Select(kv => new CourseManifestNode(kv.Key, kv.Value.Hash))
                .ToList();
            return new CourseManifestSnapshot(nodes);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new CourseManifestSnapshot(Array.Empty<CourseManifestNode>());
        }
    }

    protected virtual async Task<NodeContentPack?> GetNodeAsync(string courseId, string nodeId, string hash, CancellationToken ct)
    {
        try
        {
            using var response = await _s3.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = $"courses/{courseId}/nodes/{nodeId}/{hash}.json" }, ct);
            using var reader = new StreamReader(response.ResponseStream);
            var body = await reader.ReadToEndAsync(ct);
            return JsonSerializer.Deserialize<NodeContentPack>(body, ContentHash.CanonicalOptions);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Local mirror of the wire shape ApTutor.ContentAdmin's S3ContentStore writes for a course
    // manifest — deliberately not a shared type/project reference (see the plan): the Shell only
    // needs nodeId+hash out of it, not ContentAdmin's whole manifest-merge machinery.
    private sealed record S3CourseManifest(int SchemaVersion, Dictionary<string, S3NodeManifestEntry> Nodes);
    private sealed record S3NodeManifestEntry(string Hash, DateTimeOffset UpdatedAt);
}
