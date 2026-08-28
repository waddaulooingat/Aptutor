namespace ApTutor.Content;

public sealed record CourseManifestNode(string NodeId, string Hash, Difficulty Difficulty);
public sealed record CourseManifestSnapshot(IReadOnlyDictionary<string, NodeManifestEntry> Nodes);

/// Pure diffing logic for pulling approved content down to a local cache (see ApTutor.Client's
/// ContentSyncService) — deliberately zero I/O so it's unit-testable without touching S3 or disk.
/// Only ever adds; a node (or a specific version of one) cached locally but no longer in the
/// manifest is left alone (this is a pull of what's approved, not a mirror that prunes local state).
public static class ContentSyncPlanner
{
    /// Returns every (node, hash) version referenced by the manifest that isn't cached locally yet.
    /// A node can carry several independently-approved versions now — the Shell keeps every one it's
    /// ever downloaded and picks among them at random when serving practice items, rather than
    /// mirroring a single "latest" pointer, so this returns one row per missing version, not one row
    /// per node.
    public static IReadOnlyList<CourseManifestNode> ComputeNodesToDownload(
        CourseManifestSnapshot manifest, Func<string, string, bool> existsLocally) =>
        manifest.Nodes
            .SelectMany(kv => kv.Value.Versions.Select(v => new CourseManifestNode(kv.Key, v.Hash, v.Difficulty)))
            .Where(n => !existsLocally(n.NodeId, n.Hash))
            .ToList();
}
