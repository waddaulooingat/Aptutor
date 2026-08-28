using ApTutor.Client.Courses;
using ApTutor.Client.Services;
using ApTutor.Content;
using ApTutor.Curriculum;
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

    private CsaCourseModule NewCourse() => new(SkillDagLoader.Load(DagPath), _contentDir);

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
        var fake = new FakeContentSyncService(new Dictionary<string, List<NodeContentPack>> { ["u1.1"] = new() { pack } });

        var result = await fake.RefreshAsync(course);

        Assert.True(result.Success);
        Assert.Equal(new[] { "u1.1" }, result.UpdatedNodeIds);
        var saved = ContentPackStore.LoadVersions(_contentDir, "u1.1");
        Assert.Single(saved);
        Assert.Equal("hello", saved[0].WalkthroughText);
    }

    [Fact]
    public async Task RefreshAsync_AlreadyUpToDate_DownloadsNothing()
    {
        var course = NewCourse();
        var pack = SamplePack("u1.1", "hello");
        ContentPackStore.SaveVersion(_contentDir, ContentHash.Compute(pack), pack); // pre-seed the exact same version locally

        var fake = new FakeContentSyncService(new Dictionary<string, List<NodeContentPack>> { ["u1.1"] = new() { pack } });
        var result = await fake.RefreshAsync(course);

        Assert.True(result.Success);
        Assert.Empty(result.UpdatedNodeIds);
    }

    [Fact]
    public async Task RefreshAsync_NewVersionAlongsideAnExistingOne_DownloadsOnlyTheNewOne_KeepsBoth()
    {
        // A node can accumulate several approved sets (see the multi-set library plan) — an earlier
        // version already cached locally must survive a refresh that brings down a newer one.
        var course = NewCourse();
        var oldPack = SamplePack("u1.1", "old set");
        ContentPackStore.SaveVersion(_contentDir, ContentHash.Compute(oldPack), oldPack);

        var newPack = SamplePack("u1.1", "new set");
        var fake = new FakeContentSyncService(new Dictionary<string, List<NodeContentPack>> { ["u1.1"] = new() { oldPack, newPack } });
        var result = await fake.RefreshAsync(course);

        Assert.True(result.Success);
        Assert.Equal(new[] { "u1.1" }, result.UpdatedNodeIds);
        var versions = ContentPackStore.LoadVersions(_contentDir, "u1.1");
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, p => p.WalkthroughText == "old set");
        Assert.Contains(versions, p => p.WalkthroughText == "new set");
    }

    [Fact]
    public async Task RefreshAsync_FetchesTheExactHashVersionedObjectTheManifestPointsAt()
    {
        var course = NewCourse();
        var pack = SamplePack("u1.1", "hello");
        var fake = new FakeContentSyncService(new Dictionary<string, List<NodeContentPack>> { ["u1.1"] = new() { pack } });

        await fake.RefreshAsync(course);

        Assert.Equal(new[] { ("u1.1", ContentHash.Compute(pack)) }, fake.RequestedNodes);
    }

    [Fact]
    public async Task RefreshAsync_S3Failure_ReturnsTheExactOfflineMessage_AndTouchesNothingLocally()
    {
        var course = NewCourse();
        var fake = new FakeContentSyncService(new Dictionary<string, List<NodeContentPack>>()) { ThrowOnManifest = true };

        var result = await fake.RefreshAsync(course);

        Assert.False(result.Success);
        Assert.Equal(
            "You are offline. This app needs an internet connection. Til then you can review the previously downloaded content.",
            result.UserMessage);
        Assert.Empty(ContentPackStore.LoadVersions(_contentDir, "u1.1"));
    }

    // RefreshNodeAsync backs the Shell's per-node right-click "Get new set" — same diff-and-download
    // logic as RefreshAsync, just filtered to one node (see the Shell-display-only/course-authoring
    // plan's Part E).
    [Fact]
    public async Task RefreshNodeAsync_OnlyDownloadsTheRequestedNode_IgnoresOtherNewNodes()
    {
        var course = NewCourse();
        var fake = new FakeContentSyncService(new Dictionary<string, List<NodeContentPack>>
        {
            ["u1.1"] = new() { SamplePack("u1.1", "hello") },
            ["u1.2"] = new() { SamplePack("u1.2", "other node") },
        });

        var result = await fake.RefreshNodeAsync(course, "u1.1");

        Assert.True(result.Success);
        Assert.Equal(new[] { "u1.1" }, result.UpdatedNodeIds);
        Assert.Single(ContentPackStore.LoadVersions(_contentDir, "u1.1"));
        Assert.Empty(ContentPackStore.LoadVersions(_contentDir, "u1.2"));
    }

    [Fact]
    public async Task RefreshNodeAsync_NodeAlreadyUpToDate_DownloadsNothing()
    {
        var course = NewCourse();
        var pack = SamplePack("u1.1", "hello");
        ContentPackStore.SaveVersion(_contentDir, ContentHash.Compute(pack), pack);
        var fake = new FakeContentSyncService(new Dictionary<string, List<NodeContentPack>> { ["u1.1"] = new() { pack } });

        var result = await fake.RefreshNodeAsync(course, "u1.1");

        Assert.True(result.Success);
        Assert.Empty(result.UpdatedNodeIds);
    }

    private sealed class FakeContentSyncService : ContentSyncService
    {
        private readonly Dictionary<string, List<NodeContentPack>> _remoteNodes;
        public bool ThrowOnManifest { get; set; }
        public List<(string NodeId, string Hash)> RequestedNodes { get; } = new();

        public FakeContentSyncService(Dictionary<string, List<NodeContentPack>> remoteNodes) : base(null!, "test-bucket") =>
            _remoteNodes = remoteNodes;

        protected override Task<CourseManifestSnapshot> GetManifestAsync(string courseId, CancellationToken ct)
        {
            if (ThrowOnManifest) throw new InvalidOperationException("simulated network failure");

            var nodes = _remoteNodes.ToDictionary(
                kv => kv.Key,
                kv => new NodeManifestEntry(kv.Value.Select(p => new NodeVersionEntry(ContentHash.Compute(p), p.GeneratedAt)).ToList()),
                StringComparer.Ordinal);
            return Task.FromResult(new CourseManifestSnapshot(nodes));
        }

        protected override Task<NodeContentPack?> GetNodeAsync(string courseId, string nodeId, Difficulty difficulty, string hash, CancellationToken ct)
        {
            RequestedNodes.Add((nodeId, hash));
            var pack = _remoteNodes.GetValueOrDefault(nodeId)?.FirstOrDefault(p => ContentHash.Compute(p) == hash);
            return Task.FromResult(pack);
        }
    }
}
