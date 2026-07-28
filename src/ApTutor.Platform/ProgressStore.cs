// Persists mastered-node ids per course to a single JSON file, keyed by CourseId, so multiple
// courses can share one progress file without stepping on each other (build-plan Phase 9 note:
// per-child profiles will eventually pick the file path; the shell just needs a path today).

using System.Text.Json;

namespace ApTutor.Platform;

public sealed class ProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string FilePath { get; }

    public ProgressStore(string? filePath = null) => FilePath = filePath ?? DefaultPath();

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApTutor", "progress.json");

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Load()
    {
        if (!File.Exists(FilePath))
            return new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

        var json = File.ReadAllText(FilePath);
        var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json)
                  ?? new Dictionary<string, List<string>>();

        return raw.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyCollection<string>)kv.Value,
            StringComparer.Ordinal);
    }

    public void Save(IReadOnlyDictionary<string, IReadOnlyCollection<string>> masteredByCourse)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var raw = masteredByCourse.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(raw, JsonOptions));
    }
}
