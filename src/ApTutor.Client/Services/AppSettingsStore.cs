using System.Text.Json;

namespace ApTutor.Client.Services;

public sealed record AppSettings(
    string? ContentBucket = null,
    string? ContentRegion = null,
    string? AwsAccessKeyId = null,
    string? AwsSecretAccessKey = null,
    // "explain it to me" experiment (explain-it-to-me-experiment branch only — see its own
    // handoff): a live Claude API key, locally-configured here rather than hardcoded in source, same
    // storage this file already uses for the AWS content credential above. Deliberately not a
    // production key-management story (no accounts/billing/metering) — that's explicitly out of
    // scope for this experiment; see the handoff.
    string? AnthropicApiKey = null)
{
    public static readonly AppSettings Empty = new();
}

/// Local, per-machine settings for the S3 content-sync test bridge — TUTORAI_CONTENT_BUCKET/REGION
/// plus a read-only AWS credential, all otherwise only readable from plain environment variables.
/// Having to re-set those in every fresh terminal window was real day-to-day friction. Stored as
/// local, unencrypted JSON under the current user's profile: no worse than the plaintext environment
/// variables this already accepted (see the plan's dev-convenience-vs-customer-facing note), and not
/// something this app hands to a real paying customer as-is — revisit if/when this ships beyond
/// internal SME use. AnthropicApiKey (added for the "explain it to me" experiment, see AppSettings'
/// own remarks) is the one exception to this file's original "no live-API-key settings" scoping —
/// accepted deliberately for that experiment, not a general precedent.
public static class AppSettingsStore
{
    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TutorAI");

    private static string PathFor(string dir) => Path.Combine(dir, "settings.json");

    public static AppSettings Load(string dir)
    {
        var path = PathFor(dir);
        if (!File.Exists(path)) return AppSettings.Empty;

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? AppSettings.Empty;
        }
        catch (JsonException)
        {
            // A corrupted local settings file should never crash the app on launch — worst case,
            // the values just fall back to unset, same as if the file never existed.
            return AppSettings.Empty;
        }
    }

    /// Same atomic-write pattern as ContentPackStore.Save (temp file + overwrite move) — a crash
    /// mid-write must never leave a truncated settings file behind.
    public static void Save(string dir, AppSettings settings)
    {
        Directory.CreateDirectory(dir);
        var finalPath = PathFor(dir);
        var tempPath = Path.Combine(dir, $".settings.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, finalPath, overwrite: true);
    }
}
