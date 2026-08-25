using ApTutor.Content;
using Xunit;

namespace ApTutor.Tests;

// Pure-function tests for the Shell's diffing logic — deliberately zero S3/disk involvement (see
// the plan for the shell-content-pull test bridge and the multi-set library plan).
public class ContentSyncPlannerTests
{
    private static CourseManifestSnapshot ManifestWith(params (string NodeId, string[] Hashes)[] nodes) =>
        new(nodes.ToDictionary(
            n => n.NodeId,
            n => new NodeManifestEntry(n.Hashes.Select(h => new NodeVersionEntry(h, DateTimeOffset.UtcNow)).ToList()),
            StringComparer.Ordinal));

    [Fact]
    public void MissingLocally_IsQueuedForDownload()
    {
        var manifest = ManifestWith(("u1.1", new[] { "hash-a" }));

        var toDownload = ContentSyncPlanner.ComputeNodesToDownload(manifest, (_, _) => false);

        Assert.Equal(new[] { ("u1.1", "hash-a") }, toDownload.Select(n => (n.NodeId, n.Hash)));
    }

    [Fact]
    public void HashPresentLocally_IsSkipped()
    {
        var manifest = ManifestWith(("u1.1", new[] { "hash-a" }));

        var toDownload = ContentSyncPlanner.ComputeNodesToDownload(manifest, (nodeId, hash) => nodeId == "u1.1" && hash == "hash-a");

        Assert.Empty(toDownload);
    }

    [Fact]
    public void NewVersionAlongsideAnExistingOne_OnlyTheNewOneIsQueued()
    {
        // A node can carry several approved sets now — only the ones not yet cached locally should
        // be downloaded, not the whole node re-fetched wholesale.
        var manifest = ManifestWith(("u1.1", new[] { "hash-old", "hash-new" }));

        var toDownload = ContentSyncPlanner.ComputeNodesToDownload(manifest, (nodeId, hash) => nodeId == "u1.1" && hash == "hash-old");

        Assert.Equal(new[] { ("u1.1", "hash-new") }, toDownload.Select(n => (n.NodeId, n.Hash)));
    }

    [Fact]
    public void MixOfNodes_OnlyQueuesTheVersionsThatNeedIt()
    {
        var manifest = ManifestWith(
            ("u1.1", new[] { "hash-a" }),       // up to date
            ("u1.2", new[] { "hash-b-new" }),   // changed
            ("u1.3", new[] { "hash-c" }));      // missing locally
        var local = new HashSet<(string NodeId, string Hash)> { ("u1.1", "hash-a"), ("u1.2", "hash-b-old") };

        var toDownload = ContentSyncPlanner.ComputeNodesToDownload(manifest, (nodeId, hash) => local.Contains((nodeId, hash)));

        Assert.Equal(new[] { "u1.2", "u1.3" }, toDownload.Select(n => n.NodeId));
    }

    [Fact]
    public void LocalOnlyNode_NotInManifest_IsNeverTouched()
    {
        // The planner only ever decides what to download based on what's IN the manifest; a node
        // cached locally that the manifest doesn't mention simply never appears in its output —
        // this is a pull, not a mirror that prunes local state.
        var manifest = new CourseManifestSnapshot(new Dictionary<string, NodeManifestEntry>());

        var toDownload = ContentSyncPlanner.ComputeNodesToDownload(manifest, (_, _) => true);

        Assert.Empty(toDownload);
    }
}
