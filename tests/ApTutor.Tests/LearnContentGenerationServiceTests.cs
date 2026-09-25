using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using ApTutor.ContentFactory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApTutor.Tests;

// Same atomic-compare-and-remove ClearJob as ContentGenerationService, for the same reason — see
// its own test file's remarks. Seeded directly via RestoreJob rather than TryStartGeneration, which
// would make a real Anthropic API call.
public class LearnContentGenerationServiceTests
{
    private static LearnContentGenerationService NewService()
    {
        var client = new ClaudeClient("unused-test-key", "unused-test-model");
        var generator = new Generator(client);
        var store = new S3ContentStore(null!, Options.Create(new S3ContentStoreOptions { Bucket = "test-bucket", Region = "us-east-1" }), NullLogger<S3ContentStore>.Instance);
        var catalog = new CourseCatalog(store, NullLogger<CourseCatalog>.Instance);
        var aiReviewer = new AiReviewer(client);
        var agentOptions = Options.Create(new AiContentAgentOptions());
        return new LearnContentGenerationService(generator, catalog, aiReviewer, store, agentOptions, NullLogger<LearnContentGenerationService>.Instance);
    }

    private static LearnContent SampleContent() => new(
        "overview", new[] { new LearnStep("step") }, Verified: false, DateTimeOffset.UtcNow, "test-model");

    [Fact]
    public void ClearJob_TwoConcurrentCallers_OnlyOneSucceeds()
    {
        var service = NewService();
        service.RestoreJob(new LearnContentJob("csa", "u1.1", GenerationStatus.Succeeded, null, SampleContent(), DateTimeOffset.UtcNow));

        var results = new bool[8];
        Parallel.For(0, results.Length, i => results[i] = service.ClearJob("csa", "u1.1", GenerationStatus.Succeeded));

        Assert.Equal(1, results.Count(r => r));
        Assert.Null(service.GetJob("csa", "u1.1"));
    }

    [Fact]
    public void ClearJob_WrongExpectedStatus_DoesNotClear()
    {
        var service = NewService();
        service.RestoreJob(new LearnContentJob("csa", "u1.1", GenerationStatus.Failed, "boom", null, DateTimeOffset.UtcNow));

        Assert.False(service.ClearJob("csa", "u1.1", GenerationStatus.Succeeded));
        Assert.NotNull(service.GetJob("csa", "u1.1"));
    }

    [Fact]
    public void RestoreJob_DoesNotClobberANewerJobAlreadyInTheSlot()
    {
        var service = NewService();
        var older = new LearnContentJob("csa", "u1.1", GenerationStatus.Succeeded, null, SampleContent(), DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = new LearnContentJob("csa", "u1.1", GenerationStatus.Running, null, null, DateTimeOffset.UtcNow);

        service.RestoreJob(newer);
        service.RestoreJob(older);

        Assert.Equal(GenerationStatus.Running, service.GetJob("csa", "u1.1")!.Status);
    }

    [Fact]
    public void TryStartGeneration_RefusesADuplicateWhileAJobIsAlreadyTracked()
    {
        var service = NewService();
        service.RestoreJob(new LearnContentJob("csa", "u1.1", GenerationStatus.Succeeded, null, SampleContent(), DateTimeOffset.UtcNow));

        Assert.False(service.TryStartGeneration("csa", "u1.1"));
    }

    [Fact]
    public void GetJob_DifferentNode_IsIndependent()
    {
        var service = NewService();
        service.RestoreJob(new LearnContentJob("csa", "u1.1", GenerationStatus.Running, null, null, DateTimeOffset.UtcNow));

        Assert.NotNull(service.GetJob("csa", "u1.1"));
        Assert.Null(service.GetJob("csa", "u1.2"));
    }
}
