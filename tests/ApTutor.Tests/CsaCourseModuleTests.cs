using ApTutor.Client.Courses;
using Xunit;

namespace ApTutor.Tests;

// No comprehensive CsaCourseModule test suite exists yet (it's been exercised end-to-end via
// visual verification under Xvfb across every phase instead) — this just covers ContentDir, added
// alongside WorldHistoryCourseModule's for the dev-only "Refresh questions" feature, which needs
// both modules to expose where their content packs live on disk.
public class CsaCourseModuleTests
{
    private static string DagPath => Path.Combine(AppContext.BaseDirectory, "apcsa-skill-dag.json");

    [Fact]
    public void ContentDir_MatchesWhatWasPassedIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aptutor-csa-contentdir-test-" + Guid.NewGuid());
        var module = new CsaCourseModule(DagPath, dir);

        Assert.Equal(dir, module.ContentDir);
    }

    [Fact]
    public void ContentDir_DefaultsUnderAppBaseDirectory()
    {
        var module = new CsaCourseModule(DagPath);

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "content", "csa"), module.ContentDir);
    }
}
