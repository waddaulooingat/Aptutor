using ApTutor.Client.Services;
using Xunit;

namespace ApTutor.Tests;

// Uses a unique, fake env var name per test — never touches a name that might collide with a real
// one (ANTHROPIC_API_KEY etc.) already set in the test-runner's own environment.
public class ConfigResolverTests : IDisposable
{
    private readonly string _envVarName = "APTUTOR_TEST_" + Guid.NewGuid().ToString("N");

    public void Dispose() => Environment.SetEnvironmentVariable(_envVarName, null);

    [Fact]
    public void EnvVarSet_WinsOverSettingsValue()
    {
        Environment.SetEnvironmentVariable(_envVarName, "from-env");

        Assert.Equal("from-env", ConfigResolver.Resolve(_envVarName, "from-settings"));
    }

    [Fact]
    public void EnvVarUnset_FallsBackToSettingsValue()
    {
        Assert.Equal("from-settings", ConfigResolver.Resolve(_envVarName, "from-settings"));
    }

    [Fact]
    public void EnvVarBlank_FallsBackToSettingsValue()
    {
        Environment.SetEnvironmentVariable(_envVarName, "   ");

        Assert.Equal("from-settings", ConfigResolver.Resolve(_envVarName, "from-settings"));
    }

    [Fact]
    public void NeitherSet_ReturnsNull() =>
        Assert.Null(ConfigResolver.Resolve(_envVarName, null));
}
