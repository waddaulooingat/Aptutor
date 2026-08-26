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

    public Task<SyncResult> RefreshAsync(ICourseModule course, CancellationToken ct = default) =>
        RefreshCoreAsync(course, nodeIdFilter: null, ct);

    /// Node-scoped counterpart to RefreshAsync — pulls only what's missing for one node instead of
    /// diffing the whole course. Backs the Shell's per-node right-click refresh, which always goes
    /// through S3 now rather than a live Claude call (see the Shell-display-only/course-authoring
    /// plan's Part E).
    public Task<SyncResult> RefreshNodeAsync(ICourseModule course, string nodeId, CancellationToken ct = default) =>
        RefreshCoreAsync(course, nodeId, ct);

    private async Task<SyncResult> RefreshCoreAsync(ICourseModule course, string? nodeIdFilter, CancellationToken ct)
    {
        try
        {
            var manifest = await GetManifestAsync(course.CourseId, ct);
            var toDownload = ContentSyncPlanner.ComputeNodesToDownload(
                manifest,
                (nodeId, hash) => ContentPackStore.VersionExists(course.ContentDir, nodeId, hash));
            if (nodeIdFilter is not null)
                toDownload = toDownload.Where(n => n.NodeId == nodeIdFilter).ToList();

            var actuallyUpdated = new List<string>();
            foreach (var node in toDownload)
            {
                // Content-addressed: the hash from the manifest diff IS the object's key, not just
                // a value to compare — see S3ContentStore's layout notes. A node can have several
                // approved versions now (see the multi-set library plan); every version listed in
                // the manifest gets its own file, never overwriting a sibling version, so the Shell
                // ends up with the node's whole approved library to pick from at serving time.
                var pack = await GetNodeAsync(course.CourseId, node.NodeId, node.Hash, ct);
                if (pack is not null)
                {
                    ContentPackStore.SaveVersion(course.ContentDir, node.Hash, pack);
                    actuallyUpdated.Add(node.NodeId);
                }
            }

            // Distinct node ids for reporting — a node with several new versions in one refresh
            // contributes one entry per version to actuallyUpdated above (each is a separate file),
            // but should only be named once here.
            var updatedNodeIds = actuallyUpdated.Distinct().ToList();

            // Named explicitly, not just counted — so it's obvious during testing whether a given
            // node actually came from S3 this run versus was already cached from before.
            if (updatedNodeIds.Count > 0)
                Console.WriteLine($"[ContentSyncService] Pulled from S3 for '{course.CourseId}': {string.Join(", ", updatedNodeIds)}");
            else
                Console.WriteLine($"[ContentSyncService] '{course.CourseId}' already up to date — nothing pulled from S3.");

            return new SyncResult(true, updatedNodeIds, null);
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
            return new CourseManifestSnapshot(manifest?.Nodes ?? new Dictionary<string, NodeManifestEntry>());
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new CourseManifestSnapshot(new Dictionary<string, NodeManifestEntry>());
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

    // Local mirror of the top-level wire shape ApTutor.ContentAdmin's S3ContentStore writes for a
    // course manifest — deliberately not a shared type/project reference to ContentAdmin's whole
    // manifest-merge machinery (see the plan). The per-node entry shape (NodeManifestEntry) IS
    // shared, from ApTutor.Content, since its backward-compatibility parsing needs to behave
    // identically on both sides — see ContentManifestWire.cs.
    private sealed record S3CourseManifest(int SchemaVersion, Dictionary<string, NodeManifestEntry> Nodes);
}
