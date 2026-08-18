using ApTutor.ContentAdmin.Services;
using Xunit;

namespace ApTutor.Tests;

// Pure-function tests for ContentManifest.cs's merge logic — the only genuinely new, non-trivial
// piece of the S3 content store migration (see the plan). No S3/network involved on purpose.
public class ManifestMergeTests
{
    [Fact]
    public void UpsertNode_OnEmptyManifest_AddsTheNode()
    {
        var empty = CourseManifest.Empty(schemaVersion: 1);
        var entry = new NodeManifestEntry("abc123", DateTimeOffset.UtcNow);

        var updated = ManifestMerge.UpsertNode(empty, "u1.1", entry);

        Assert.Equal(1, updated.SchemaVersion);
        Assert.Single(updated.Nodes);
        Assert.Equal(entry, updated.Nodes["u1.1"]);
    }

    [Fact]
    public void UpsertNode_ExistingNode_OverwritesInPlace_LeavesOthersAlone()
    {
        var first = new NodeManifestEntry("hash-v1", DateTimeOffset.UtcNow.AddMinutes(-5));
        var other = new NodeManifestEntry("hash-other", DateTimeOffset.UtcNow.AddMinutes(-5));
        var manifest = ManifestMerge.UpsertNode(
            ManifestMerge.UpsertNode(CourseManifest.Empty(1), "u1.1", first), "u2.1", other);

        var second = new NodeManifestEntry("hash-v2", DateTimeOffset.UtcNow);
        var updated = ManifestMerge.UpsertNode(manifest, "u1.1", second);

        Assert.Equal(2, updated.Nodes.Count);
        Assert.Equal(second, updated.Nodes["u1.1"]);
        Assert.Equal(other, updated.Nodes["u2.1"]); // untouched
    }

    [Fact]
    public void UpsertNode_DoesNotMutateTheOriginalManifest()
    {
        var original = CourseManifest.Empty(1);
        ManifestMerge.UpsertNode(original, "u1.1", new NodeManifestEntry("h", DateTimeOffset.UtcNow));

        Assert.Empty(original.Nodes);
    }

    [Fact]
    public void UpsertCourse_OnEmptyTopLevelManifest_AddsTheCourse()
    {
        var empty = TopLevelManifest.Empty(1);
        var entry = new CourseIndexEntry("manifest-hash", DateTimeOffset.UtcNow);

        var updated = ManifestMerge.UpsertCourse(empty, "csa", entry);

        Assert.Single(updated.Courses);
        Assert.Equal(entry, updated.Courses["csa"]);
    }

    [Fact]
    public void UpsertCourse_ExistingCourse_OverwritesInPlace_LeavesOthersAlone()
    {
        var manifest = ManifestMerge.UpsertCourse(
            ManifestMerge.UpsertCourse(TopLevelManifest.Empty(1), "csa", new CourseIndexEntry("h1", DateTimeOffset.UtcNow)),
            "worldhistory", new CourseIndexEntry("h2", DateTimeOffset.UtcNow));

        var updatedEntry = new CourseIndexEntry("h1-updated", DateTimeOffset.UtcNow);
        var updated = ManifestMerge.UpsertCourse(manifest, "csa", updatedEntry);

        Assert.Equal(2, updated.Courses.Count);
        Assert.Equal(updatedEntry, updated.Courses["csa"]);
        Assert.Equal("h2", updated.Courses["worldhistory"].ManifestHash);
    }
}
