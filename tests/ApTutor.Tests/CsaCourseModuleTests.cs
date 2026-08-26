using ApTutor.Client.Courses;
using ApTutor.Curriculum;
using Xunit;

namespace ApTutor.Tests;

// No comprehensive CsaCourseModule test suite exists yet (it's been exercised end-to-end via
// visual verification under Xvfb across every phase instead) — this just covers ContentDir, and
// that construction uses the SkillGraph passed in rather than reloading from disk.
public class CsaCourseModuleTests
{
    private static SkillGraph SampleGraph =>
        SkillDagLoader.Load(Path.Combine(AppContext.BaseDirectory, "apcsa-skill-dag.json"));

    [Fact]
    public void Ctor_UsesTheGraphPassedIn_DoesNotReloadFromDisk()
    {
        var graph = SampleGraph;

        var module = new CsaCourseModule(graph, Path.GetTempPath());

        Assert.Same(graph, module.Dag);
    }

    [Fact]
    public void ContentDir_MatchesWhatWasPassedIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aptutor-csa-contentdir-test-" + Guid.NewGuid());
        var module = new CsaCourseModule(SampleGraph, dir);

        Assert.Equal(dir, module.ContentDir);
    }

    [Fact]
    public void ContentDir_DefaultsUnderAppBaseDirectory()
    {
        var module = new CsaCourseModule(SampleGraph);

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "content", "csa"), module.ContentDir);
    }
}
