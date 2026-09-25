using System.Net;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApTutor.Tests;

// Part A of the AI Content Agent interim stopgap (see the handoff) — gap detection is pure S3-manifest
// reading (safe, real network calls never happen: see FakeS3ContentStore). RunScanAsync's triggering
// is tested ONLY in ways that never let a genuine TryStartGeneration reach a real node — either via
// maxGenerations: 0 (nothing is ever started) or by pre-seeding every gap's job slot as already
// in-flight (TryStartGeneration then fails fast on TryAdd, before it would ever call the real
// generator) — same "never make a real Anthropic API call from a test" discipline
// ContentGenerationServiceTests documents for exactly the same reason.
public class AutonomousContentAgentServiceTests
{
    private static SkillDag Structure(params (string Id, int Unit)[] nodes) => new(
        Meta: new DagMeta("Computer Science A", 1),
        ScenePrimitives: new Dictionary<string, string>(),
        Units: nodes.Select(n => n.Unit).Distinct().Select(u => new UnitInfo(u, $"Unit {u}")).ToArray(),
        Nodes: nodes.Select(n => new DagNode(n.Id, n.Unit, NodeType.Concept, n.Id, Array.Empty<string>(), "")).ToArray());

    private static NodeContentPack SamplePack(string nodeId, Difficulty difficulty) => new(
        CourseId: "csa", NodeId: nodeId, ExampleId: "generated", WalkthroughText: "text",
        PracticeItems: Array.Empty<Platform.PracticeItem>(), WalkthroughSteps: Array.Empty<Platform.VisualStep>(),
        Verified: true, GeneratedAt: DateTimeOffset.UtcNow, Model: "test-model", Difficulty: difficulty);

    private static (AutonomousContentAgentService Agent, FakeS3ContentStore Store, CourseCatalog Catalog, ContentGenerationService Generation) NewAgent()
    {
        var store = new FakeS3ContentStore();
        var catalog = new CourseCatalog(store, NullLogger<CourseCatalog>.Instance);
        var client = new ClaudeClient("unused-test-key", "unused-test-model");
        var generator = new Generator(client);
        var aiReviewer = new AiReviewer(client);
        var agentOptions = Options.Create(new AiContentAgentOptions());
        var generation = new ContentGenerationService(generator, catalog, aiReviewer, store, agentOptions, NullLogger<ContentGenerationService>.Instance);
        var agent = new AutonomousContentAgentService(catalog, store, generation);
        return (agent, store, catalog, generation);
    }

    [Fact]
    public async Task FindGapsAsync_BrandNewCourse_EveryNodeAtEveryDifficultyIsAGap()
    {
        var (agent, store, catalog, _) = NewAgent();
        await store.ApproveStructureAsync("csa", Structure(("u1.1", 1)));
        await catalog.RefreshAsync();

        var gaps = await agent.FindGapsAsync("csa");

        Assert.Equal(3, gaps.Count); // one node x three difficulties
        Assert.Contains(gaps, g => g.Difficulty == Difficulty.Easy);
        Assert.Contains(gaps, g => g.Difficulty == Difficulty.Medium);
        Assert.Contains(gaps, g => g.Difficulty == Difficulty.Hard);
    }

    [Fact]
    public async Task FindGapsAsync_NodeApprovedAtOneDifficultyOnly_OtherTwoStillGaps()
    {
        var (agent, store, catalog, _) = NewAgent();
        await store.ApproveStructureAsync("csa", Structure(("u1.1", 1)));
        await store.ApproveNodeAsync("csa", "u1.1", SamplePack("u1.1", Difficulty.Medium));
        await catalog.RefreshAsync();

        var gaps = await agent.FindGapsAsync("csa");

        Assert.Equal(2, gaps.Count);
        Assert.DoesNotContain(gaps, g => g.Difficulty == Difficulty.Medium);
    }

    [Fact]
    public async Task FindGapsAsync_EveryDifficultyApproved_NoGaps()
    {
        var (agent, store, catalog, _) = NewAgent();
        await store.ApproveStructureAsync("csa", Structure(("u1.1", 1)));
        foreach (var difficulty in new[] { Difficulty.Easy, Difficulty.Medium, Difficulty.Hard })
            await store.ApproveNodeAsync("csa", "u1.1", SamplePack("u1.1", difficulty));
        await catalog.RefreshAsync();

        var gaps = await agent.FindGapsAsync("csa");

        Assert.Empty(gaps);
    }

    [Fact]
    public async Task FindGapsAsync_CourseWithNoStructureYet_ReturnsEmpty()
    {
        var (agent, _, _, _) = NewAgent();

        var gaps = await agent.FindGapsAsync("ghost-course");

        Assert.Empty(gaps);
    }

    [Fact]
    public async Task RunScanAsync_MaxGenerationsZero_ReportsGapsButTriggersNothing()
    {
        var (agent, store, catalog, _) = NewAgent();
        await store.ApproveStructureAsync("csa", Structure(("u1.1", 1)));
        await catalog.RefreshAsync();

        var result = await agent.RunScanAsync("csa", maxGenerations: 0);

        Assert.Equal(3, result.GapsFound);
        Assert.Equal(0, result.Triggered);
    }

    [Fact]
    public async Task RunScanAsync_GapAlreadyInFlight_CountsAsSkippedNotTriggered()
    {
        var (agent, store, catalog, generation) = NewAgent();
        await store.ApproveStructureAsync("csa", Structure(("u1.1", 1)));
        await catalog.RefreshAsync();

        // Pre-seed every gap's job slot as already running so RunScanAsync's TryStartGeneration
        // calls fail fast on TryAdd (never reach the real generator) — see class remarks.
        foreach (var difficulty in new[] { Difficulty.Easy, Difficulty.Medium, Difficulty.Hard })
            generation.RestoreJob(new GenerationJob("csa", "u1.1", difficulty, false, GenerationStatus.Running, null, null, DateTimeOffset.UtcNow));

        var result = await agent.RunScanAsync("csa", maxGenerations: 20);

        Assert.Equal(3, result.GapsFound);
        Assert.Equal(0, result.Triggered);
        Assert.Equal(3, result.SkippedAlreadyInFlight);
    }

    /// Same minimal in-memory fake as CourseCatalogTests/S3ContentStoreTests use — overrides only the
    /// two leaf methods that actually touch IAmazonS3.
    private sealed class FakeS3ContentStore : S3ContentStore
    {
        private static readonly JsonSerializerOptions JsonOptions = ContentHash.CanonicalOptions;
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
