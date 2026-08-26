using System.Net;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using ApTutor.Curriculum;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApTutor.Tests;

// Exercises CourseCatalog.RefreshAsync's real S3-discovery logic end to end against a fake that
// overrides just the two IAmazonS3-touching leaf methods on S3ContentStore — same seam pattern as
// S3ContentStoreTests. RefreshAsync goes through S3ContentStore's real ApproveStructureAsync to seed
// state, so the wire format is guaranteed correct rather than hand-reconstructed.
public class CourseCatalogTests
{
    private static SkillDag SampleStructure(string courseTitle) => new(
        Meta: new DagMeta(courseTitle, 1),
        ScenePrimitives: new Dictionary<string, string>(),
        Units: new[] { new UnitInfo(1, "Unit 1") },
        Nodes: new[] { new DagNode("u1.1", 1, NodeType.Concept, "Intro", Array.Empty<string>(), "") });

    [Fact]
    public async Task RefreshAsync_NoCoursesInS3_ProducesAnEmptyCatalog()
    {
        var store = new FakeS3ContentStore();
        var catalog = new CourseCatalog(store, NullLogger<CourseCatalog>.Instance);

        await catalog.RefreshAsync();

        Assert.Empty(catalog.All);
    }

    [Fact]
    public async Task RefreshAsync_CourseWithApprovedStructure_PopulatesGraphAndDisplayName()
    {
        var store = new FakeS3ContentStore();
        await store.ApproveStructureAsync("csa", SampleStructure("Computer Science A"));
        var catalog = new CourseCatalog(store, NullLogger<CourseCatalog>.Instance);

        await catalog.RefreshAsync();

        Assert.True(catalog.TryGet("csa", out var course));
        Assert.Equal("Computer Science A", course.DisplayName);
        Assert.NotNull(course.Graph);
        Assert.True(course.Graph!.Exists("u1.1"));
    }

    [Fact]
    public async Task RefreshAsync_CourseInIndexButNoStructureApprovedYet_GraphIsNull_DisplayNameFallsBackToCourseId()
    {
        // Reachable once "Create new course" (approving a unit list, before any DAG exists) lands —
        // simulated here directly since that flow doesn't exist yet: a node was approved for a
        // course whose structure was never approved, which is enough to put the course in the
        // top-level index via the exact same ReindexCourseInTopLevelManifestAsync tail
        // ApproveStructureAsync uses.
        var store = new FakeS3ContentStore();
        await store.ApproveNodeAsync("newcourse", "u1.1", SamplePack("u1.1"));
        var catalog = new CourseCatalog(store, NullLogger<CourseCatalog>.Instance);

        await catalog.RefreshAsync();

        Assert.True(catalog.TryGet("newcourse", out var course));
        Assert.Null(course.Graph);
        Assert.Equal("newcourse", course.DisplayName);
    }

    [Fact]
    public async Task RefreshAsync_CalledAgainAfterAnApproval_PicksUpTheChange()
    {
        var store = new FakeS3ContentStore();
        var catalog = new CourseCatalog(store, NullLogger<CourseCatalog>.Instance);
        await catalog.RefreshAsync();
        Assert.Empty(catalog.All);

        await store.ApproveStructureAsync("csa", SampleStructure("Computer Science A"));
        await catalog.RefreshAsync();

        Assert.True(catalog.TryGet("csa", out _));
    }

    private static NodeContentPack SamplePack(string nodeId) => new(
        CourseId: "newcourse",
        NodeId: nodeId,
        ExampleId: "generated",
        WalkthroughText: "text",
        PracticeItems: Array.Empty<Platform.PracticeItem>(),
        WalkthroughSteps: Array.Empty<Platform.VisualStep>(),
        Verified: true,
        GeneratedAt: DateTimeOffset.UtcNow,
        Model: "test-model");

    /// Same minimal in-memory fake as S3ContentStoreTests' — overrides only the two leaf methods
    /// that actually touch IAmazonS3.
    private sealed class FakeS3ContentStore : S3ContentStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
        private readonly Dictionary<string, (string Body, string ETag)> _objects = new(StringComparer.Ordinal);
        private int _etagCounter;

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
            if (_objects.TryGetValue(key, out var existingForCheck))
            {
                if (ifNoneMatch == "*")
                    throw new AmazonS3Exception("already exists", ErrorType.Sender, "PreconditionFailed", "req-id", HttpStatusCode.PreconditionFailed);
                if (ifMatch is not null && !string.Equals(ifMatch, existingForCheck.ETag, StringComparison.Ordinal))
                    throw new AmazonS3Exception("etag mismatch", ErrorType.Sender, "PreconditionFailed", "req-id", HttpStatusCode.PreconditionFailed);
            }

            _objects[key] = (body, $"\"etag-{++_etagCounter}\"");
            return Task.CompletedTask;
        }
    }
}
