using ApTutor.ContentAdmin.Services;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApTutor.Tests;

// UnitStructureGenerationService's ClearJob is the same atomic compare-and-remove
// ContentGenerationService's is, for the same reason — these tests mirror
// ContentGenerationServiceTests exactly, seeding jobs directly via RestoreJob rather than going
// through TryStartGeneration, since that would make a real Anthropic API call.
public class UnitStructureGenerationServiceTests
{
    private static UnitStructureGenerationService NewService()
    {
        var client = new ClaudeClient("unused-test-key", "unused-test-model");
        var generator = new Generator(client);
        var store = new S3ContentStore(null!, Options.Create(new S3ContentStoreOptions { Bucket = "test-bucket", Region = "us-east-1" }), NullLogger<S3ContentStore>.Instance);
        var catalog = new CourseCatalog(store, NullLogger<CourseCatalog>.Instance);
        return new UnitStructureGenerationService(generator, catalog, NullLogger<UnitStructureGenerationService>.Instance);
    }

    private static IReadOnlyList<DagNode> SampleNodes => new[]
    {
        new DagNode("u2.1", 2, NodeType.Concept, "Trans-Saharan trade routes", Array.Empty<string>(), ""),
    };

    [Fact]
    public void ClearJob_TwoConcurrentCallers_OnlyOneSucceeds()
    {
        var service = NewService();
        service.RestoreJob(new UnitStructureJob("worldhistory", 2, "Networks of Exchange", StructureGenerationStatus.Succeeded, null, SampleNodes, DateTimeOffset.UtcNow));

        var results = new bool[8];
        Parallel.For(0, results.Length, i => results[i] = service.ClearJob("worldhistory", 2, StructureGenerationStatus.Succeeded));

        Assert.Equal(1, results.Count(r => r));
        Assert.Null(service.GetJob("worldhistory", 2));
    }

    [Fact]
    public void ClearJob_WrongExpectedStatus_DoesNotClear()
    {
        var service = NewService();
        service.RestoreJob(new UnitStructureJob("worldhistory", 2, "Networks of Exchange", StructureGenerationStatus.Failed, "boom", null, DateTimeOffset.UtcNow));

        var cleared = service.ClearJob("worldhistory", 2, StructureGenerationStatus.Succeeded);

        Assert.False(cleared);
        Assert.NotNull(service.GetJob("worldhistory", 2));
    }

    [Fact]
    public void ClearJob_NoJobForUnit_ReturnsFalse()
    {
        var service = NewService();

        Assert.False(service.ClearJob("worldhistory", 99, StructureGenerationStatus.Succeeded));
    }

    [Fact]
    public void RestoreJob_DoesNotClobberANewerJobAlreadyInTheSlot()
    {
        var service = NewService();
        var older = new UnitStructureJob("worldhistory", 2, "Networks of Exchange", StructureGenerationStatus.Succeeded, null, SampleNodes, DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = new UnitStructureJob("worldhistory", 2, "Networks of Exchange", StructureGenerationStatus.Running, null, null, DateTimeOffset.UtcNow);

        service.RestoreJob(newer);
        service.RestoreJob(older); // simulates: Approve claimed+removed `older`, its S3 write failed,
                                    // but a fresh Generate already started before the restore ran.

        Assert.Equal(StructureGenerationStatus.Running, service.GetJob("worldhistory", 2)!.Status);
    }

    [Fact]
    public void TryStartGeneration_RefusesADuplicateWhileAJobIsAlreadyTracked()
    {
        var service = NewService();
        service.RestoreJob(new UnitStructureJob("worldhistory", 2, "Networks of Exchange", StructureGenerationStatus.Succeeded, null, SampleNodes, DateTimeOffset.UtcNow));

        var started = service.TryStartGeneration("worldhistory", 2, "Networks of Exchange", guidance: null);

        Assert.False(started);
    }

    [Fact]
    public void GetJob_DifferentUnitNumberSameCourse_IsIndependentOfAnotherUnitsJob()
    {
        var service = NewService();
        service.RestoreJob(new UnitStructureJob("worldhistory", 2, "Networks of Exchange", StructureGenerationStatus.Running, null, null, DateTimeOffset.UtcNow));

        // A job tracked for unit 2 must not be visible under unit 3's key — confirms Key() is
        // scoped per (course, unit), not just per course. Doesn't call TryStartGeneration on the
        // free slot: that would fire a real background Claude call (see this file's own comment).
        Assert.NotNull(service.GetJob("worldhistory", 2));
        Assert.Null(service.GetJob("worldhistory", 3));
    }
}
