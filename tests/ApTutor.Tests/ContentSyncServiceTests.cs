using ApTutor.Client.Courses;
using ApTutor.Client.Services;
using ApTutor.Content;
using ApTutor.Platform;
using ApTutor.Scene;
using Xunit;

namespace ApTutor.Tests;

// Exercises ContentSyncService.RefreshAsync's real diff-and-download logic end to end via a fake
// that overrides just the two IAmazonS3-touching leaf methods (GetManifestAsync/GetNodeAsync),
// mirroring the same seam pattern used for ApTutor.ContentAdmin's S3ContentStoreTests.
public class ContentSyncServiceTests : IDisposable
{
    private static string DagPath => Path.Combine(AppContext.BaseDirectory, "apcsa-skill-dag.json");

    private readonly string _contentDir = Path.Combine(Path.GetTempPath(), "aptutor-sync-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_contentDir)) Directory.Delete(_contentDir, recursive: true);
    }

    private CsaCourseModule NewCourse() => new(DagPath, _contentDir);

    private static NodeContentPack SamplePack(string nodeId, string text) => new(
        CourseId: "csa",
        NodeId: nodeId,
        ExampleId: "generated",
        WalkthroughText: text,
        PracticeItems: Array.Empty<PracticeItem>(),
        WalkthroughSteps: Array.Empty<VisualStep>(),
        Verified: true,
        GeneratedAt: DateTimeOffset.UtcNow,
        Model: "test-model");

    [Fact]
    public async Task RefreshAsync_NothingCachedLocally_DownloadsEveryManifestNode()
    {
        var course = NewCourse();
        var pack = SamplePack("u1.1", "hello");
        var fake = new FakeContentSyncService(new Dictionary<string, NodeContentPack> { ["u1.1"] = pack });

        var result = await fake.RefreshAsync(course);

        Assert.True(result.Success);
        Assert.Equal(new[] { "u1.1" }, result.UpdatedNodeIds);
        var saved = ContentPackStore.TryLoad(_contentDir, "u1.1");
        Assert.NotNull(saved);
        Assert.Equal("hello", saved!.WalkthroughText);
    }

    [Fact]
    public async Task RefreshAsync_AlreadyUpToDate_DownloadsNothing()
    {
        var course = NewCourse();
        var pack = SamplePack("u1.1", "hello");
        ContentPackStore.Save(_contentDir, pack); // pre-seed the exact same content locally

        var fake = new FakeContentSyncService(new Dictionary<string, NodeContentPack> { ["u1.1"] = pack });
        var result = await fake.RefreshAsync(course);

        Assert.True(result.Success);
        Assert.Empty(result.UpdatedNodeIds);
    }

    [Fact]
    public async Task RefreshAsync_ChangedContent_DownloadsAndOverwritesTheLocalCopy()
    {
        var course = NewCourse();
        ContentPackStore.Save(_contentDir, SamplePack("u1.1", "old text"));

        var fake = new FakeContentSyncService(new Dictionary<string, NodeContentPack> { ["u1.1"] = SamplePack("u1.1", "new text") });
        var result = await fake.RefreshAsync(course);

        Assert.True(result.Success);
        Assert.Equal(new[] { "u1.1" }, result.UpdatedNodeIds);
        Assert.Equal("new text", ContentPackStore.TryLoad(_contentDir, "u1.1")!.WalkthroughText);
    }

    [Fact]
    public async Task RefreshAsync_S3Failure_ReturnsTheExactOfflineMessage_AndTouchesNothingLocally()
    {
        var course = NewCourse();
        var fake = new FakeContentSyncService(new Dictionary<string, NodeContentPack>()) { ThrowOnManifest = true };

        var result = await fake.RefreshAsync(course);

        Assert.False(result.Success);
        Assert.Equal(
            "You are offline. This app needs an internet connection. Til then you can review the previously downloaded content.",
            result.UserMessage);
        Assert.Null(ContentPackStore.TryLoad(_contentDir, "u1.1"));
    }

    private sealed class FakeContentSyncService : ContentSyncService
    {
        private readonly Dictionary<string, NodeContentPack> _remoteNodes;
        public bool ThrowOnManifest { get; set; }

        public FakeContentSyncService(Dictionary<string, NodeContentPack> remoteNodes) : base(null!, "test-bucket") =>
            _remoteNodes = remoteNodes;

        protected override Task<CourseManifestSnapshot> GetManifestAsync(string courseId, CancellationToken ct)
        {
            if (ThrowOnManifest) throw new InvalidOperationException("simulated network failure");

            var nodes = _remoteNodes.Select(kv => new CourseManifestNode(kv.Key, ContentHash.Compute(kv.Value))).ToList();
            return Task.FromResult(new CourseManifestSnapshot(nodes));
        }

        protected override Task<NodeContentPack?> GetNodeAsync(string courseId, string nodeId, CancellationToken ct) =>
            Task.FromResult(_remoteNodes.GetValueOrDefault(nodeId));
    }
}
