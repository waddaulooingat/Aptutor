using ApTutor.Client.Services;
using Xunit;

namespace ApTutor.Tests;

public class AppSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aptutor-settings-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_NoFileYet_ReturnsEmpty() =>
        Assert.Equal(AppSettings.Empty, AppSettingsStore.Load(_dir));

    [Fact]
    public void Save_ThenLoad_RoundTripsExactly()
    {
        var settings = new AppSettings(
            ContentBucket: "tutor-ai-content",
            ContentRegion: "us-east-1",
            AwsAccessKeyId: "AKIATEST",
            AwsSecretAccessKey: "secret-test");

        AppSettingsStore.Save(_dir, settings);
        var loaded = AppSettingsStore.Load(_dir);

        Assert.Equal(settings, loaded);
    }

    [Fact]
    public void Save_OnlySomeFieldsSet_LeavesTheRestNull()
    {
        var settings = new AppSettings(ContentBucket: "tutor-ai-content", ContentRegion: "us-east-1");

        AppSettingsStore.Save(_dir, settings);
        var loaded = AppSettingsStore.Load(_dir);

        Assert.Null(loaded.AwsAccessKeyId);
        Assert.Equal("tutor-ai-content", loaded.ContentBucket);
    }

    [Fact]
    public void Save_Overwrites_DoesNotLeaveTempFilesBehind()
    {
        AppSettingsStore.Save(_dir, new AppSettings(ContentBucket: "first"));
        AppSettingsStore.Save(_dir, new AppSettings(ContentBucket: "second"));

        Assert.Equal("second", AppSettingsStore.Load(_dir).ContentBucket);
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Load_CorruptedFile_ReturnsEmpty_DoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not valid json");

        Assert.Equal(AppSettings.Empty, AppSettingsStore.Load(_dir));
    }
}
