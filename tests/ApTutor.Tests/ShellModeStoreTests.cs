using ApTutor.Client.Services;
using Xunit;

namespace ApTutor.Tests;

public class ShellModeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aptutor-shellmode-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_NoFileYet_DefaultsToQuiz() =>
        Assert.Equal(ShellMode.Quiz, ShellModeStore.Load(_dir));

    [Fact]
    public void Save_ThenLoad_RoundTripsLearn()
    {
        ShellModeStore.Save(_dir, ShellMode.Learn);

        Assert.Equal(ShellMode.Learn, ShellModeStore.Load(_dir));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsQuiz()
    {
        ShellModeStore.Save(_dir, ShellMode.Learn); // start non-default to prove Save actually overwrites
        ShellModeStore.Save(_dir, ShellMode.Quiz);

        Assert.Equal(ShellMode.Quiz, ShellModeStore.Load(_dir));
    }

    [Fact]
    public void Load_CorruptedFile_DefaultsToQuiz_DoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mode.json"), "{ not valid json");

        Assert.Equal(ShellMode.Quiz, ShellModeStore.Load(_dir));
    }

    [Fact]
    public void Save_Overwrites_DoesNotLeaveTempFilesBehind()
    {
        ShellModeStore.Save(_dir, ShellMode.Learn);
        ShellModeStore.Save(_dir, ShellMode.Quiz);

        Assert.Equal(new[] { "mode.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }
}
