using ApTutor.Client.Courses;
using Xunit;

namespace ApTutor.Tests;

// World History pilot (BUILD-PLAN.md "Course model"): exercises the second ICourseModule end to
// end — DAG loading, identity, the trademark guardrail on DisplayName, and the Phase 7
// AuthoredStepProvider/FileContentSource wiring's documented "no content generated yet" behavior.
// Mirrors the shape of CsaCourseModule's own tests (see CsaCourseModuleTests if present) but for
// a course with no fixture bank and no live tracer.
public class WorldHistoryCourseModuleTests
{
    private static string DagPath => Path.Combine(AppContext.BaseDirectory, "apwh-skill-dag.json");

    private static WorldHistoryCourseModule NewModule(string? contentDir = null) =>
        new(DagPath, contentDir ?? Path.Combine(Path.GetTempPath(), "aptutor-wh-tests-" + Guid.NewGuid()));

    [Fact]
    public void Ctor_LoadsDagCorrectly()
    {
        var module = NewModule();

        Assert.Equal(module.Dag.Dag.Meta.NodeCount, module.Dag.Dag.Nodes.Count);
        Assert.True(module.Dag.Exists("u1.1"));
    }

    [Fact]
    public void CourseId_IsWorldHistory() =>
        Assert.Equal("worldhistory", NewModule().CourseId);

    [Fact]
    public void DisplayName_IsWorldHistory_NotApWorldHistory()
    {
        var displayName = NewModule().DisplayName;

        Assert.Equal("World History", displayName);
        // Trademark guardrail (BUILD-PLAN.md "Guardrails"): no "AP"/"Advanced Placement" in the
        // user-facing display name, mirroring CsaCourseModule.DisplayName.
        Assert.DoesNotContain("AP", displayName);
    }

    [Fact]
    public void Grader_IsNull() =>
        Assert.Null(NewModule().Grader);

    [Fact]
    public void StepProvider_DoesNotSupportLiveInput() =>
        Assert.False(NewModule().StepProvider.SupportsLiveInput);

    [Fact]
    public void Content_GetPracticeItems_NoContentGeneratedYet_ReturnsEmptyList_DoesNotThrow()
    {
        var module = NewModule();

        var items = module.Content.GetPracticeItems("u1.1");

        Assert.Empty(items);
    }

    [Fact]
    public void ContentDir_MatchesWhatWasPassedIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aptutor-wh-contentdir-test-" + Guid.NewGuid());
        var module = NewModule(dir);

        Assert.Equal(dir, module.ContentDir);
    }
}
