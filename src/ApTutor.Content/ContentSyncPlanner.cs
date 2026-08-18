namespace ApTutor.Content;

public sealed record CourseManifestNode(string NodeId, string Hash);
public sealed record CourseManifestSnapshot(IReadOnlyList<CourseManifestNode> Nodes);

/// Pure diffing logic for pulling approved content down to a local cache (see ApTutor.Client's
/// ContentSyncService) — deliberately zero I/O so it's unit-testable without touching S3 or disk.
/// Only ever adds/updates; a node cached locally but no longer in the manifest is left alone (this
/// is a pull of what's approved, not a mirror that prunes local state).
public static class ContentSyncPlanner
{
    /// localHash returns null if the node isn't cached locally at all.
    public static IReadOnlyList<string> ComputeNodesToDownload(CourseManifestSnapshot manifest, Func<string, string?> localHash) =>
        manifest.Nodes
            .Where(n => localHash(n.NodeId) != n.Hash)
            .Select(n => n.NodeId)
            .ToList();
}
