using ApTutor.Content;
using ApTutor.Platform;
using ApTutor.Scene;
using Xunit;

namespace ApTutor.Tests;

// Phase 7: the content-pack schema (shared between the factory that writes it and the runtime
// readers that serve it) and the "only Verified: true ever ships" gate.
public class ContentPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aptutor-content-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static NodeContentPack SamplePack(string nodeId, bool verified) => new(
        CourseId: "csa",
        NodeId: nodeId,
        ExampleId: "generated",
        WalkthroughText: "A short explanation.",
        PracticeItems: new[] { new PracticeItem($"{nodeId}-q1", nodeId, "prompt?", new[] { "a", "b", "c", "d" }, 1, "because") },
        WalkthroughSteps: new[] { new VisualStep(0, "first step", new SceneDelta(new SceneOp[] { new LineHighlight(1) })) },
        Verified: verified,
        GeneratedAt: DateTimeOffset.UtcNow,
        Model: "test-model");

    [Fact]
    public void Save_ThenLoad_RoundTripsExactly()
    {
        var pack = SamplePack("u1.1", verified: true);

        ContentPackStore.Save(_dir, pack);
        var loaded = ContentPackStore.TryLoad(_dir, "u1.1");

        Assert.NotNull(loaded);
        Assert.Equal(pack.NodeId, loaded!.NodeId);
        Assert.Equal(pack.WalkthroughText, loaded.WalkthroughText);
        Assert.Equal(pack.Verified, loaded.Verified);
        Assert.Single(loaded.PracticeItems);
        Assert.Equal(pack.PracticeItems[0].Prompt, loaded.PracticeItems[0].Prompt);
        Assert.Single(loaded.WalkthroughSteps);
        Assert.Equal(pack.WalkthroughSteps[0].Delta.Ops[0], loaded.WalkthroughSteps[0].Delta.Ops[0]);
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull() =>
        Assert.Null(ContentPackStore.TryLoad(_dir, "ghost"));

    [Fact]
    public void LoadAll_ReturnsEveryPack_SortedByNodeId()
    {
        ContentPackStore.Save(_dir, SamplePack("u2.1", verified: true));
        ContentPackStore.Save(_dir, SamplePack("u1.1", verified: false));

        var all = ContentPackStore.LoadAll(_dir);

        Assert.Equal(new[] { "u1.1", "u2.1" }, all.Select(p => p.NodeId));
    }

    [Fact]
    public void LoadAll_SkipsMalformedFile_InsteadOfThrowing()
    {
        ContentPackStore.Save(_dir, SamplePack("u1.1", verified: true));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "u9.9.json"), "{ not valid json");

        var all = ContentPackStore.LoadAll(_dir);

        Assert.Single(all);
        Assert.Equal("u1.1", all[0].NodeId);
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        ContentPackStore.Save(_dir, SamplePack("u1.1", verified: false));
        ContentPackStore.Delete(_dir, "u1.1");

        Assert.Null(ContentPackStore.TryLoad(_dir, "u1.1"));
    }

    [Fact]
    public void FileContentSource_UnverifiedPack_PracticeItemsAreEmpty_AndWalkthroughTextThrows()
    {
        ContentPackStore.Save(_dir, SamplePack("u1.1", verified: false));
        var source = new FileContentSource(_dir);

        Assert.Empty(source.GetPracticeItems("u1.1"));
        Assert.Throws<InvalidOperationException>(() => source.GetWalkthroughText("u1.1", "generated"));
    }

    [Fact]
    public void FileContentSource_VerifiedPack_ServesContent()
    {
        ContentPackStore.Save(_dir, SamplePack("u1.1", verified: true));
        var source = new FileContentSource(_dir);

        Assert.Equal("A short explanation.", source.GetWalkthroughText("u1.1", "generated"));
        Assert.Single(source.GetPracticeItems("u1.1"));
    }

    [Fact]
    public void AuthoredStepProvider_UnverifiedPack_Throws()
    {
        ContentPackStore.Save(_dir, SamplePack("u1.1", verified: false));
        var provider = new AuthoredStepProvider(_dir);

        Assert.Throws<InvalidOperationException>(() => provider.GetSteps("u1.1", "generated"));
    }

    [Fact]
    public void AuthoredStepProvider_VerifiedPack_ServesWalkthroughSteps()
    {
        ContentPackStore.Save(_dir, SamplePack("u1.1", verified: true));
        var provider = new AuthoredStepProvider(_dir);

        Assert.False(provider.SupportsLiveInput);
        var steps = provider.GetSteps("u1.1", "generated");
        Assert.Single(steps);
        Assert.Equal("first step", steps[0].Caption);
    }

    // ---- Version-aware storage (see the multi-set library plan) ----

    [Fact]
    public void SaveVersion_ThenLoadVersions_RoundTrips()
    {
        var pack = SamplePack("u1.1", verified: true);

        ContentPackStore.SaveVersion(_dir, "hash-a", pack);
        var versions = ContentPackStore.LoadVersions(_dir, "u1.1");

        Assert.Single(versions);
        Assert.Equal(pack.WalkthroughText, versions[0].WalkthroughText);
    }

    [Fact]
    public void SaveVersion_DoesNotCollideWithTheSingleFileLayoutForTheSameNode()
    {
        // "<nodeId>.json" (flat file) and "<nodeId>/" (versions subfolder) can coexist for the same
        // node without clobbering each other — this matters during the transition, where a node
        // might have both bundled dev content and synced multi-set content.
        var flatPack = SamplePack("u1.1", verified: true) with { WalkthroughText = "flat" };
        var versionedPack = SamplePack("u1.1", verified: true) with { WalkthroughText = "versioned" };

        ContentPackStore.Save(_dir, flatPack);
        ContentPackStore.SaveVersion(_dir, "hash-a", versionedPack);

        Assert.Equal("flat", ContentPackStore.TryLoad(_dir, "u1.1")!.WalkthroughText);
        Assert.Equal("versioned", ContentPackStore.LoadVersions(_dir, "u1.1").Single().WalkthroughText);
    }

    [Fact]
    public void VersionExists_ReflectsWhatsBeenSaved()
    {
        Assert.False(ContentPackStore.VersionExists(_dir, "u1.1", "hash-a"));

        ContentPackStore.SaveVersion(_dir, "hash-a", SamplePack("u1.1", verified: true));

        Assert.True(ContentPackStore.VersionExists(_dir, "u1.1", "hash-a"));
        Assert.False(ContentPackStore.VersionExists(_dir, "u1.1", "hash-b"));
    }

    [Fact]
    public void LoadVersions_NoVersionsSavedYet_ReturnsEmpty() =>
        Assert.Empty(ContentPackStore.LoadVersions(_dir, "never-synced"));

    [Fact]
    public void FileContentSource_MultipleVerifiedVersions_PracticeItemsComeFromExactlyOneOfThem()
    {
        var setA = SamplePack("u1.1", verified: true) with
        {
            PracticeItems = new[] { new PracticeItem("a-q1", "u1.1", "prompt A", new[] { "x", "y" }, 0, "because") },
        };
        var setB = SamplePack("u1.1", verified: true) with
        {
            PracticeItems = new[] { new PracticeItem("b-q1", "u1.1", "prompt B", new[] { "x", "y" }, 0, "because") },
        };
        ContentPackStore.SaveVersion(_dir, "hash-a", setA);
        ContentPackStore.SaveVersion(_dir, "hash-b", setB);
        var source = new FileContentSource(_dir);

        var items = source.GetPracticeItems("u1.1");

        Assert.Single(items); // one whole set's items, not both pooled together
        Assert.True(items[0].Id is "a-q1" or "b-q1");
    }

    [Fact]
    public void FileContentSource_UnverifiedVersionsOnly_FallsBackAsIfNoneExisted()
    {
        ContentPackStore.SaveVersion(_dir, "hash-a", SamplePack("u1.1", verified: false));
        var source = new FileContentSource(_dir);

        Assert.Empty(source.GetPracticeItems("u1.1"));
    }

    [Fact]
    public void FileContentSource_NoVersionsSynced_FallsBackToTheSingleFileLayout()
    {
        // Bundled/dev-generated content that predates or never goes through Content Admin's S3 sync
        // path must keep working unchanged.
        ContentPackStore.Save(_dir, SamplePack("u1.1", verified: true));
        var source = new FileContentSource(_dir);

        Assert.Single(source.GetPracticeItems("u1.1"));
        Assert.Equal("A short explanation.", source.GetWalkthroughText("u1.1", "generated"));
    }

    [Fact]
    public void FileContentSource_MultipleVersions_WalkthroughTextComesFromTheMostRecentlyGeneratedOne()
    {
        var older = SamplePack("u1.1", verified: true) with
        {
            WalkthroughText = "older explanation", GeneratedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        var newer = SamplePack("u1.1", verified: true) with
        {
            WalkthroughText = "newer explanation", GeneratedAt = DateTimeOffset.UtcNow,
        };
        ContentPackStore.SaveVersion(_dir, "hash-old", older);
        ContentPackStore.SaveVersion(_dir, "hash-new", newer);
        var source = new FileContentSource(_dir);

        // Deterministic, unlike practice items — a topic's explanation switching at random between
        // visits would read as a bug, not variety.
        Assert.Equal("newer explanation", source.GetWalkthroughText("u1.1", "generated"));
        Assert.Equal("newer explanation", source.GetWalkthroughText("u1.1", "generated"));
    }
}
