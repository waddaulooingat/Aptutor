using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using ApTutor.ContentFactory;
using ApTutor.Platform;
using ApTutor.Scene;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApTutor.Tests;

// ContentGenerationService's ClearJob is the atomic compare-and-remove that stops two
// near-simultaneous Approve/Reject requests from both acting on the same generated draft (see the
// plan). These tests seed jobs directly via the (already-public, production) RestoreJob method
// rather than going through TryStartGeneration, since that would make a real Anthropic API call.
public class ContentGenerationServiceTests
{
    private static ContentGenerationService NewService()
    {
        var client = new ClaudeClient("unused-test-key", "unused-test-model");
        var generator = new Generator(client);
        // CourseCatalog isn't actually exercised by these tests (ClearJob doesn't touch it) — just
        // needs to exist to satisfy ContentGenerationService's constructor. IAmazonS3 is never
        // called since nothing here invokes CourseCatalog.RefreshAsync.
        var store = new S3ContentStore(null!, Options.Create(new S3ContentStoreOptions { Bucket = "test-bucket", Region = "us-east-1" }), NullLogger<S3ContentStore>.Instance);
        var catalog = new CourseCatalog(store, NullLogger<CourseCatalog>.Instance);
        return new ContentGenerationService(generator, catalog, NullLogger<ContentGenerationService>.Instance);
    }

    private static NodeContentPack SamplePack(string nodeId) => new(
        CourseId: "csa",
        NodeId: nodeId,
        ExampleId: "generated",
        WalkthroughText: "text",
        PracticeItems: Array.Empty<PracticeItem>(),
        WalkthroughSteps: Array.Empty<VisualStep>(),
        Verified: false,
        GeneratedAt: DateTimeOffset.UtcNow,
        Model: "test-model");

    [Fact]
    public void ClearJob_TwoConcurrentCallers_OnlyOneSucceeds()
    {
        var service = NewService();
        service.RestoreJob(new GenerationJob("csa", "u1.1", GenerationStatus.Succeeded, null, SamplePack("u1.1"), DateTimeOffset.UtcNow));

        var results = new bool[8];
        Parallel.For(0, results.Length, i => results[i] = service.ClearJob("csa", "u1.1", GenerationStatus.Succeeded));

        Assert.Equal(1, results.Count(r => r));
        Assert.Null(service.GetJob("csa", "u1.1"));
    }

    [Fact]
    public void ClearJob_WrongExpectedStatus_DoesNotClear()
    {
        var service = NewService();
        service.RestoreJob(new GenerationJob("csa", "u1.1", GenerationStatus.Failed, "boom", null, DateTimeOffset.UtcNow));

        var cleared = service.ClearJob("csa", "u1.1", GenerationStatus.Succeeded);

        Assert.False(cleared);
        Assert.NotNull(service.GetJob("csa", "u1.1"));
    }

    [Fact]
    public void ClearJob_NoJobForNode_ReturnsFalse()
    {
        var service = NewService();

        Assert.False(service.ClearJob("csa", "ghost", GenerationStatus.Succeeded));
    }

    [Fact]
    public void RestoreJob_DoesNotClobberANewerJobAlreadyInTheSlot()
    {
        var service = NewService();
        var older = new GenerationJob("csa", "u1.1", GenerationStatus.Succeeded, null, SamplePack("u1.1"), DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = new GenerationJob("csa", "u1.1", GenerationStatus.Running, null, null, DateTimeOffset.UtcNow);

        service.RestoreJob(newer);
        service.RestoreJob(older); // simulates: Approve claimed+removed `older`, its S3 write failed,
                                    // but a fresh Generate already started before the restore ran.

        Assert.Equal(GenerationStatus.Running, service.GetJob("csa", "u1.1")!.Status);
    }

    [Fact]
    public void TryStartGeneration_RefusesADuplicateWhileAJobIsAlreadyTracked()
    {
        var service = NewService();
        service.RestoreJob(new GenerationJob("csa", "u1.1", GenerationStatus.Succeeded, null, SamplePack("u1.1"), DateTimeOffset.UtcNow));

        var started = service.TryStartGeneration("csa", "u1.1");

        Assert.False(started);
    }
}
