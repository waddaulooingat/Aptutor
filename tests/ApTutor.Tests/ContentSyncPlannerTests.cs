using ApTutor.Content;
using Xunit;

namespace ApTutor.Tests;

// Pure-function tests for the Shell's diffing logic — deliberately zero S3/disk involvement (see
// the plan for the shell-content-pull test bridge).
public class ContentSyncPlannerTests
{
    [Fact]
    public void MissingLocally_IsQueuedForDownload()
    {
        var manifest = new CourseManifestSnapshot(new[] { new CourseManifestNode("u1.1", "hash-a") });

        var toDownload = ContentSyncPlanner.ComputeNodesToDownload(manifest, _ => null);

        Assert.Equal(new[] { "u1.1" }, toDownload.Select(n => n.NodeId));
    }

    [Fact]
    public void HashMatches_IsSkipped()
    {
        var manifest = new CourseManifestSnapshot(new[] { new CourseManifestNode("u1.1", "hash-a") });

        var toDownload = ContentSyncPlanner.ComputeNodesToDownload(manifest, _ => "hash-a");

        Assert.Empty(toDownload);
    }

    [Fact]
    public void HashDiffers_IsQueuedForDownload()
    {
        var manifest = new CourseManifestSnapshot(new[] { new CourseManifestNode("u1.1", "hash-new") });

        var toDownload = ContentSyncPlanner.ComputeNodesToDownload(manifest, _ => "hash-old");

        Assert.Equal(new[] { "u1.1" }, toDownload.Select(n => n.NodeId));
    }

    [Fact]
    public void MixOfNodes_OnlyQueuesTheOnesThatNeedIt()
    {
        var manifest = new CourseManifestSnapshot(new[]
        {
            new CourseManifestNode("u1.1", "hash-a"), // up to date
            new CourseManifestNode("u1.2", "hash-b-new"), // changed
            new CourseManifestNode("u1.3", "hash-c"), // missing locally
        });
        var local = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["u1.1"] = "hash-a",
            ["u1.2"] = "hash-b-old",
        };

        var toDownload = ContentSyncPlanner.ComputeNodesToDownload(manifest, id => local.GetValueOrDefault(id));

        Assert.Equal(new[] { "u1.2", "u1.3" }, toDownload.Select(n => n.NodeId));
    }

    [Fact]
    public void LocalOnlyNode_NotInManifest_IsNeverTouched()
    {
        // The planner only ever decides what to download based on what's IN the manifest; a node
        // cached locally that the manifest doesn't mention simply never appears in its output —
        // this is a pull, not a mirror that prunes local state.
        var manifest = new CourseManifestSnapshot(Array.Empty<CourseManifestNode>());

        var toDownload = ContentSyncPlanner.ComputeNodesToDownload(manifest, _ => "some-local-hash");

        Assert.Empty(toDownload);
    }
}
