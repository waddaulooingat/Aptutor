using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using Xunit;

namespace ApTutor.Tests;

// Pure-function tests for ContentManifest.cs's merge logic — the only genuinely new, non-trivial
// piece of the S3 content store migration (see the plan). No S3/network involved on purpose.
public class ManifestMergeTests
{
    [Fact]
    public void UpsertNode_OnEmptyManifest_AddsTheNodeWithOneVersion()
    {
        var empty = CourseManifest.Empty(schemaVersion: 1);
        var updatedAt = DateTimeOffset.UtcNow;

        var updated = ManifestMerge.UpsertNode(empty, "u1.1", "abc123", updatedAt, Difficulty.Medium);

        Assert.Equal(1, updated.SchemaVersion);
        Assert.Single(updated.Nodes);
        Assert.Equal(new[] { new NodeVersionEntry("abc123", updatedAt, Difficulty.Medium) }, updated.Nodes["u1.1"].Versions);
    }

    [Fact]
    public void UpsertNode_NewHashForAnAlreadyLiveNode_AppendsAVersion_LeavesTheOldOneInPlace()
    {
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);
        var manifest = ManifestMerge.UpsertNode(CourseManifest.Empty(1), "u1.1", "hash-v1", first, Difficulty.Medium);

        var second = DateTimeOffset.UtcNow;
        var updated = ManifestMerge.UpsertNode(manifest, "u1.1", "hash-v2", second, Difficulty.Medium);

        Assert.Equal(2, updated.Nodes["u1.1"].Versions.Count);
        Assert.Contains(new NodeVersionEntry("hash-v1", first, Difficulty.Medium), updated.Nodes["u1.1"].Versions);
        Assert.Contains(new NodeVersionEntry("hash-v2", second, Difficulty.Medium), updated.Nodes["u1.1"].Versions);
        Assert.Equal("hash-v2", updated.Nodes["u1.1"].Latest.Hash);
    }

    [Fact]
    public void UpsertNode_SameHashAlreadyPresent_IsAHarmlessNoOp_DoesNotDuplicate()
    {
        var when = DateTimeOffset.UtcNow;
        var manifest = ManifestMerge.UpsertNode(CourseManifest.Empty(1), "u1.1", "hash-v1", when, Difficulty.Medium);

        var updated = ManifestMerge.UpsertNode(manifest, "u1.1", "hash-v1", DateTimeOffset.UtcNow.AddMinutes(1), Difficulty.Medium);

        Assert.Single(updated.Nodes["u1.1"].Versions);
    }

    [Fact]
    public void UpsertNode_ExistingNode_LeavesOtherNodesAlone()
    {
        var when = DateTimeOffset.UtcNow.AddMinutes(-5);
        var manifest = ManifestMerge.UpsertNode(
            ManifestMerge.UpsertNode(CourseManifest.Empty(1), "u1.1", "hash-v1", when, Difficulty.Medium), "u2.1", "hash-other", when, Difficulty.Medium);

        var updated = ManifestMerge.UpsertNode(manifest, "u1.1", "hash-v2", DateTimeOffset.UtcNow, Difficulty.Medium);

        Assert.Equal(2, updated.Nodes.Count);
        Assert.Equal("hash-other", updated.Nodes["u2.1"].Latest.Hash); // untouched
    }

    [Fact]
    public void UpsertNode_DoesNotMutateTheOriginalManifest()
    {
        var original = CourseManifest.Empty(1);
        ManifestMerge.UpsertNode(original, "u1.1", "h", DateTimeOffset.UtcNow, Difficulty.Medium);

        Assert.Empty(original.Nodes);
    }

    [Fact]
    public void UpsertNode_DifferentDifficultiesForSameNode_BothAppear_LatestForIsolatesEach()
    {
        var whenEasy = DateTimeOffset.UtcNow.AddMinutes(-5);
        var whenHard = DateTimeOffset.UtcNow;
        var manifest = ManifestMerge.UpsertNode(
            ManifestMerge.UpsertNode(CourseManifest.Empty(1), "u1.1", "hash-easy", whenEasy, Difficulty.Easy),
            "u1.1", "hash-hard", whenHard, Difficulty.Hard);

        Assert.Equal(2, manifest.Nodes["u1.1"].Versions.Count);
        Assert.Equal("hash-easy", manifest.Nodes["u1.1"].LatestFor(Difficulty.Easy)!.Hash);
        Assert.Equal("hash-hard", manifest.Nodes["u1.1"].LatestFor(Difficulty.Hard)!.Hash);
        Assert.Null(manifest.Nodes["u1.1"].LatestFor(Difficulty.Medium));
    }

    [Fact]
    public void SetStructure_OnACourseWithNoStructureYet_SetsThePointer()
    {
        var when = DateTimeOffset.UtcNow;
        var manifest = ManifestMerge.SetStructure(CourseManifest.Empty(1), "structure-hash-1", when);

        Assert.Equal("structure-hash-1", manifest.StructureHash);
        Assert.Equal(when, manifest.StructureUpdatedAt);
    }

    [Fact]
    public void SetStructure_ReplacesThePreviousPointer_UnlikeUpsertNodeItDoesNotAccumulate()
    {
        var manifest = ManifestMerge.SetStructure(CourseManifest.Empty(1), "hash-v1", DateTimeOffset.UtcNow.AddMinutes(-5));

        var updated = ManifestMerge.SetStructure(manifest, "hash-v2", DateTimeOffset.UtcNow);

        Assert.Equal("hash-v2", updated.StructureHash);
    }

    [Fact]
    public void SetStructure_LeavesNodesAlone()
    {
        var manifest = ManifestMerge.UpsertNode(CourseManifest.Empty(1), "u1.1", "node-hash", DateTimeOffset.UtcNow, Difficulty.Medium);

        var updated = ManifestMerge.SetStructure(manifest, "structure-hash", DateTimeOffset.UtcNow);

        Assert.True(updated.Nodes.ContainsKey("u1.1"));
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
