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
}
