using ApTutor.Client.Courses;
using ApTutor.Curriculum;
using Xunit;

namespace ApTutor.Tests;

// Replaces WorldHistoryCourseModuleTests — World History never had any bespoke logic beyond what
// GenericCourseModule now provides for any course (see the Shell-display-only/course-authoring
// plan), so this exercises the exact same behavior via the World History DAG, plus one test proving
// it's genuinely generic rather than still secretly World-History-shaped.
public class GenericCourseModuleTests
{
    private static string WorldHistoryDagPath => Path.Combine(AppContext.BaseDirectory, "apwh-skill-dag.json");

    private static GenericCourseModule NewWorldHistoryModule(string? contentDir = null) => new(
        "worldhistory",
        "World History",
        SkillDagLoader.Load(WorldHistoryDagPath),
        contentDir ?? Path.Combine(Path.GetTempPath(), "aptutor-generic-course-tests-" + Guid.NewGuid()));

    [Fact]
    public void Ctor_UsesTheGraphPassedIn_DoesNotReloadFromDisk()
    {
        var graph = SkillDagLoader.Load(WorldHistoryDagPath);

        var module = new GenericCourseModule("worldhistory", "World History", graph, Path.GetTempPath());

        Assert.Same(graph, module.Dag);
        Assert.True(module.Dag.Exists("u1.1"));
    }

    [Fact]
    public void CourseIdAndDisplayName_AreWhateverWasPassedIn_NotHardcoded()
    {
        var module = new GenericCourseModule("physics1", "AP Physics 1", SkillDagLoader.Load(WorldHistoryDagPath), Path.GetTempPath());

        Assert.Equal("physics1", module.CourseId);
        Assert.Equal("AP Physics 1", module.DisplayName);
    }

    [Fact]
    public void Grader_IsNull() =>
        Assert.Null(NewWorldHistoryModule().Grader);

    [Fact]
    public void Primitives_IsEmpty() =>
        Assert.Empty(NewWorldHistoryModule().Primitives);

    [Fact]
    public void StepProvider_DoesNotSupportLiveInput() =>
        Assert.False(NewWorldHistoryModule().StepProvider.SupportsLiveInput);

    [Fact]
    public void Content_GetPracticeItems_NoContentGeneratedYet_ReturnsEmptyList_DoesNotThrow()
    {
        var module = NewWorldHistoryModule();

        var items = module.Content.GetPracticeItems("u1.1");

        Assert.Empty(items);
    }

    [Fact]
    public void ContentDir_MatchesWhatWasPassedIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aptutor-generic-course-contentdir-test-" + Guid.NewGuid());
        var module = NewWorldHistoryModule(dir);

        Assert.Equal(dir, module.ContentDir);
    }
}
