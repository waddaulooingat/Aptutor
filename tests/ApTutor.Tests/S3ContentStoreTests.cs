using System.Net;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using ApTutor.Platform;
using ApTutor.Scene;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApTutor.Tests;

// Exercises the real conditional-write retry loop in S3ContentStore (ApproveNodeAsync ->
// UpdateWithRetryAsync) end to end, via a fake that overrides just the two IAmazonS3-touching leaf
// methods rather than hand-stubbing the entire (huge) IAmazonS3 interface. See S3ContentStore.cs
// for why those two methods are a protected virtual seam.
public class S3ContentStoreTests
{
    private static NodeContentPack SamplePack(string nodeId, string walkthroughText) => new(
        CourseId: "csa",
        NodeId: nodeId,
        ExampleId: "generated",
        WalkthroughText: walkthroughText,
        PracticeItems: new[] { new PracticeItem($"{nodeId}-q1", nodeId, "prompt?", new[] { "a", "b", "c", "d" }, 1, "because") },
        WalkthroughSteps: Array.Empty<VisualStep>(),
        Verified: true,
        GeneratedAt: DateTimeOffset.UtcNow,
        Model: "test-model");

    [Fact]
    public async Task ApproveNodeAsync_FirstEverWriteForACourse_CreatesNodeAndBothManifests()
    {
        var store = new FakeS3ContentStore();

        await store.ApproveNodeAsync("csa", "u1.1", SamplePack("u1.1", "first explanation"));

        var courseManifest = await store.GetCourseManifestAsync("csa");
        Assert.True(courseManifest.Nodes.ContainsKey("u1.1"));

        var live = await store.TryGetLiveNodeAsync("csa", "u1.1");
        Assert.NotNull(live);
        Assert.Equal("first explanation", live!.WalkthroughText);
    }

    [Fact]
    public async Task ApproveNodeAsync_SecondNodeInSameCourse_LeavesTheFirstNodesManifestEntryAlone()
    {
        var store = new FakeS3ContentStore();

        await store.ApproveNodeAsync("csa", "u1.1", SamplePack("u1.1", "first"));
        await store.ApproveNodeAsync("csa", "u1.2", SamplePack("u1.2", "second"));

        var manifest = await store.GetCourseManifestAsync("csa");
        Assert.Equal(2, manifest.Nodes.Count);
        Assert.True(manifest.Nodes.ContainsKey("u1.1"));
        Assert.True(manifest.Nodes.ContainsKey("u1.2"));
    }

    [Fact]
    public async Task ApproveNodeAsync_UnchangedContent_ProducesTheSameHash_ChangedContent_ProducesADifferentHash()
    {
        var store = new FakeS3ContentStore();

        // Re-approving the exact same pack instance (same GeneratedAt and all) must hash
        // identically — re-approving a pack with different content must not.
        var samePack = SamplePack("u1.1", "same text");

        await store.ApproveNodeAsync("csa", "u1.1", samePack);
        var hashAfterFirst = (await store.GetCourseManifestAsync("csa")).Nodes["u1.1"].Hash;

        await store.ApproveNodeAsync("csa", "u1.1", samePack);
        var hashAfterRepeat = (await store.GetCourseManifestAsync("csa")).Nodes["u1.1"].Hash;
        Assert.Equal(hashAfterFirst, hashAfterRepeat);

        var changedPack = samePack with { WalkthroughText = "different text now" };
        await store.ApproveNodeAsync("csa", "u1.1", changedPack);
        var hashAfterChange = (await store.GetCourseManifestAsync("csa")).Nodes["u1.1"].Hash;
        Assert.NotEqual(hashAfterFirst, hashAfterChange);
    }

    [Fact]
    public async Task ApproveNodeAsync_LosesAConcurrentWriteRaceOnce_RetriesAndStillSucceeds()
    {
        var store = new FakeS3ContentStore { ConflictsToSimulateOnCourseManifest = 1 };

        await store.ApproveNodeAsync("csa", "u1.1", SamplePack("u1.1", "text"));

        // One failed attempt (simulated conflict) + one successful retry.
        Assert.Equal(2, store.PutAttemptsOnCourseManifest);
        var manifest = await store.GetCourseManifestAsync("csa");
        Assert.True(manifest.Nodes.ContainsKey("u1.1"));
    }

    [Fact]
    public async Task ApproveNodeAsync_ExhaustsRetryBudget_ThrowsRatherThanSilentlyLosingData()
    {
        var store = new FakeS3ContentStore { ConflictsToSimulateOnCourseManifest = 100 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ApproveNodeAsync("csa", "u1.1", SamplePack("u1.1", "text")));
    }

    /// Overrides only the two leaf methods that actually call IAmazonS3, so every other line of
    /// S3ContentStore (including the retry loop) runs for real. IAmazonS3 itself is never invoked —
    /// passing null! for it is safe because nothing here calls the base implementations.
    private sealed class FakeS3ContentStore : S3ContentStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
        private readonly Dictionary<string, (string Body, string ETag)> _objects = new(StringComparer.Ordinal);
        private int _etagCounter;
        private int _courseManifestConflictsSoFar;

        public int ConflictsToSimulateOnCourseManifest { get; set; }
        public int PutAttemptsOnCourseManifest { get; private set; }

        public FakeS3ContentStore()
            : base(null!, Options.Create(new S3ContentStoreOptions { Bucket = "test-bucket", Region = "us-east-1" }), NullLogger<S3ContentStore>.Instance)
        {
        }

        protected override Task<(T? Value, string? ETag)> TryGetObjectAsync<T>(string key, CancellationToken ct) where T : class
        {
            if (_objects.TryGetValue(key, out var existing))
                return Task.FromResult<(T?, string?)>((JsonSerializer.Deserialize<T>(existing.Body, JsonOptions), existing.ETag));
            return Task.FromResult<(T?, string?)>((null, null));
        }

        protected override Task PutObjectAsync(string key, string body, string? ifMatch, string? ifNoneMatch, CancellationToken ct)
        {
            var isCourseManifest = key.EndsWith("/manifest.json", StringComparison.Ordinal);
            if (isCourseManifest) PutAttemptsOnCourseManifest++;

            if (isCourseManifest && _courseManifestConflictsSoFar < ConflictsToSimulateOnCourseManifest)
            {
                _courseManifestConflictsSoFar++;
                // Simulate another writer racing in between this caller's GET and PUT: bump the
                // stored ETag so the ifMatch this caller is about to send is now stale.
                if (_objects.TryGetValue(key, out var current))
                    _objects[key] = (current.Body, NextETag());
                throw new AmazonS3Exception("simulated conflict", ErrorType.Sender, "PreconditionFailed", "req-id", HttpStatusCode.PreconditionFailed);
            }

            if (_objects.TryGetValue(key, out var existingForCheck))
            {
                if (ifNoneMatch == "*")
                    throw new AmazonS3Exception("already exists", ErrorType.Sender, "PreconditionFailed", "req-id", HttpStatusCode.PreconditionFailed);
                if (ifMatch is not null && !string.Equals(ifMatch, existingForCheck.ETag, StringComparison.Ordinal))
                    throw new AmazonS3Exception("etag mismatch", ErrorType.Sender, "PreconditionFailed", "req-id", HttpStatusCode.PreconditionFailed);
            }

            _objects[key] = (body, NextETag());
            return Task.CompletedTask;
        }

        private string NextETag() => $"\"etag-{++_etagCounter}\"";
    }
}
