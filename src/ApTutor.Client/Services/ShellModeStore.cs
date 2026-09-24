using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApTutor.Client.Services;

/// Learn/Quiz — a top-level, always-switchable mode (see the learn-quiz-mode-switch plan's Part A),
/// not per-node gating. Quiz is unchanged today's-only behavior (practice items, mastery tracking,
/// weak-node resurfacing); Learn shows teaching content instead and never touches mastery.
public enum ShellMode { Learn, Quiz }

/// Persists the student's last-selected mode locally — same %LOCALAPPDATA%\TutorAI\ location and
/// plain-JSON-file convention as AppSettingsStore/AttemptLogStore, not synced anywhere. Defaults to
/// Quiz (today's only behavior) when nothing's been saved yet, so an existing install with no prior
/// mode choice doesn't silently start a returning student in a mode they never picked.
public static class ShellModeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private static string PathFor(string dir) => Path.Combine(dir, "mode.json");

    public static ShellMode Load(string dir)
    {
        var path = PathFor(dir);
        if (!File.Exists(path)) return ShellMode.Quiz;

        try
        {
            return JsonSerializer.Deserialize<ShellMode>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            // A corrupted local file should never crash the app on launch — worst case, mode
            // falls back to Quiz, same as if the file never existed.
            return ShellMode.Quiz;
        }
    }

    /// Same atomic-write pattern as AppSettingsStore.Save (temp file + overwrite move) — a crash
    /// mid-write must never leave a truncated file behind.
    public static void Save(string dir, ShellMode mode)
    {
        Directory.CreateDirectory(dir);
        var finalPath = PathFor(dir);
        var tempPath = Path.Combine(dir, $".mode.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(mode, JsonOptions));
        File.Move(tempPath, finalPath, overwrite: true);
    }
}
