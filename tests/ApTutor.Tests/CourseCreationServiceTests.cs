using ApTutor.ContentAdmin.Services;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ApTutor.Tests;

// CourseCreationService's ClearJob is the same atomic compare-and-remove
// ContentGenerationService's/UnitStructureGenerationService's is, for the same reason — these
// tests mirror those files exactly, seeding jobs directly via RestoreJob rather than going through
// TryStartGeneration, since that would make a real Anthropic API call.
public class CourseCreationServiceTests
{
    private static CourseCreationService NewService()
    {
        var client = new ClaudeClient("unused-test-key", "unused-test-model");
        var generator = new Generator(client);
        return new CourseCreationService(generator, NullLogger<CourseCreationService>.Instance);
    }

    private static readonly IReadOnlyList<UnitInfo> SampleUnits = new[] { new UnitInfo(1, "Kinematics") };

    [Fact]
    public void ClearJob_TwoConcurrentCallers_OnlyOneSucceeds()
    {
        var service = NewService();
        service.RestoreJob(new CourseCreationJob("physics1", "AP Physics 1", "Physics 1", CourseCreationStatus.Succeeded, null, SampleUnits, DateTimeOffset.UtcNow));

        var results = new bool[8];
        Parallel.For(0, results.Length, i => results[i] = service.ClearJob("physics1", CourseCreationStatus.Succeeded));

        Assert.Equal(1, results.Count(r => r));
        Assert.Null(service.GetJob("physics1"));
    }

    [Fact]
    public void ClearJob_WrongExpectedStatus_DoesNotClear()
    {
        var service = NewService();
        service.RestoreJob(new CourseCreationJob("physics1", "AP Physics 1", "Physics 1", CourseCreationStatus.Failed, "boom", null, DateTimeOffset.UtcNow));

        var cleared = service.ClearJob("physics1", CourseCreationStatus.Succeeded);

        Assert.False(cleared);
        Assert.NotNull(service.GetJob("physics1"));
    }

    [Fact]
    public void ClearJob_NoJobForCourse_ReturnsFalse()
    {
        var service = NewService();

        Assert.False(service.ClearJob("ghost", CourseCreationStatus.Succeeded));
    }

    [Fact]
    public void RestoreJob_DoesNotClobberANewerJobAlreadyInTheSlot()
    {
        var service = NewService();
        var older = new CourseCreationJob("physics1", "AP Physics 1", "Physics 1", CourseCreationStatus.Succeeded, null, SampleUnits, DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = new CourseCreationJob("physics1", "AP Physics 1", "Physics 1", CourseCreationStatus.Running, null, null, DateTimeOffset.UtcNow);

        service.RestoreJob(newer);
        service.RestoreJob(older); // simulates: Approve claimed+removed `older`, its S3 write failed,
                                    // but a fresh Generate already started before the restore ran.

        Assert.Equal(CourseCreationStatus.Running, service.GetJob("physics1")!.Status);
    }

    [Fact]
    public void TryStartGeneration_RefusesADuplicateWhileAJobIsAlreadyTracked()
    {
        var service = NewService();
        service.RestoreJob(new CourseCreationJob("physics1", "AP Physics 1", "Physics 1", CourseCreationStatus.Succeeded, null, SampleUnits, DateTimeOffset.UtcNow));

        var started = service.TryStartGeneration("physics1", "AP Physics 1", "Physics 1", guidance: null);

        Assert.False(started);
    }

    [Fact]
    public void GetJob_DifferentCourseId_IsIndependentOfAnotherCoursesJob()
    {
        var service = NewService();
        service.RestoreJob(new CourseCreationJob("physics1", "AP Physics 1", "Physics 1", CourseCreationStatus.Running, null, null, DateTimeOffset.UtcNow));

        Assert.NotNull(service.GetJob("physics1"));
        Assert.Null(service.GetJob("chemistry1"));
    }
}
