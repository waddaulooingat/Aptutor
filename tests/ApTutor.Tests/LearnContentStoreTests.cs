using ApTutor.Content;
using Xunit;

namespace ApTutor.Tests;

public class LearnContentStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aptutor-learncontent-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static LearnContent SampleContent() => new(
        Overview: "This node covers X.",
        Steps: new[] { new LearnStep("Step one", "Detail one"), new LearnStep("Step two") },
        Verified: true,
        GeneratedAt: DateTimeOffset.UtcNow,
        Model: "test-model");

    [Fact]
    public void TryLoad_NoFileYet_ReturnsNull() =>
        Assert.Null(LearnContentStore.TryLoad(_dir, "u1.1"));

    [Fact]
    public void Save_ThenTryLoad_RoundTripsExactly()
    {
        var content = SampleContent();

        LearnContentStore.Save(_dir, "u1.1", content);
        var loaded = LearnContentStore.TryLoad(_dir, "u1.1");

        Assert.NotNull(loaded);
        Assert.Equal(content.Overview, loaded!.Overview);
        Assert.Equal(2, loaded.Steps.Count);
        Assert.Equal("Step one", loaded.Steps[0].Caption);
        Assert.Equal("Detail one", loaded.Steps[0].Detail);
        Assert.Null(loaded.Steps[1].Detail);
        Assert.True(loaded.Verified);
    }

    [Fact]
    public void Save_DifferentNodes_DoNotCollide()
    {
        LearnContentStore.Save(_dir, "u1.1", SampleContent() with { Overview = "first" });
        LearnContentStore.Save(_dir, "u1.2", SampleContent() with { Overview = "second" });

        Assert.Equal("first", LearnContentStore.TryLoad(_dir, "u1.1")!.Overview);
        Assert.Equal("second", LearnContentStore.TryLoad(_dir, "u1.2")!.Overview);
    }

    [Fact]
    public void TryLoad_CorruptedFile_ReturnsNull_DoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LearnContentStore.PathFor(_dir, "u1.1"), "{ not valid json");

        Assert.Null(LearnContentStore.TryLoad(_dir, "u1.1"));
    }

    [Fact]
    public void Save_Overwrites_DoesNotLeaveTempFilesBehind()
    {
        LearnContentStore.Save(_dir, "u1.1", SampleContent() with { Overview = "first" });
        LearnContentStore.Save(_dir, "u1.1", SampleContent() with { Overview = "second" });

        Assert.Equal("second", LearnContentStore.TryLoad(_dir, "u1.1")!.Overview);
        Assert.Equal(new[] { "u1.1.learn.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }
}
