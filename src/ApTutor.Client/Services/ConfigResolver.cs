namespace ApTutor.Client.Services;

/// A plain environment variable always wins when set — preserves the existing behavior for anyone
/// who prefers env vars (CI, automation, a one-off override) — falling back to the locally-saved
/// AppSettings value otherwise (see AppSettingsStore).
public static class ConfigResolver
{
    public static string? Resolve(string envVarName, string? settingsValue)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVarName);
        return string.IsNullOrWhiteSpace(fromEnv) ? settingsValue : fromEnv;
    }
}
