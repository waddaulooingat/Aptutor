using System.Text.Json;

namespace ApTutor.Content;

/// One step of a node's teaching content — flexible enough to serve both subject shapes the
/// learn-quiz-mode-switch plan calls for without two separate schemas: a worked-example step
/// (Physics/Math — Caption states the step, Detail carries the reasoning/equation) or a structured
/// section (English/History — Caption is a subheading, Detail is the explanatory paragraph). Which
/// shape a given node's steps take is left to the generation prompt to judge from the node/course
/// context, the same way existing practice-item generation already adapts wording per course
/// without a separate per-subject toggle.
public sealed record LearnStep(string Caption, string? Detail = null);

/// A node's teaching content for Learn mode (see that plan's Part B) — generated and reviewed
/// alongside practice items, not replacing them, and NOT difficulty-scoped: a node has exactly one
/// current explanation, reused across every difficulty, unlike the practice-item library's
/// accumulate-many-sets-per-difficulty model. That's also why this isn't content-addressed/hashed
/// like NodeContentPack — there's no "keep every past version" concept here, just the current one.
public sealed record LearnContent(
    string Overview,
    IReadOnlyList<LearnStep> Steps,
    bool Verified,
    DateTimeOffset GeneratedAt,
    string Model);

/// Local single-file cache for a node's learn content — same on-disk shape/atomic-write convention
/// as ContentPackStore, deliberately much simpler (one file per node, no version subdirectory) since
/// there's nothing to diff or accumulate.
public static class LearnContentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string PathFor(string contentDir, string nodeId) => Path.Combine(contentDir, $"{nodeId}.learn.json");

    public static LearnContent? TryLoad(string contentDir, string nodeId)
    {
        var path = PathFor(contentDir, nodeId);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<LearnContent>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(string contentDir, string nodeId, LearnContent content)
    {
        Directory.CreateDirectory(contentDir);
        var finalPath = PathFor(contentDir, nodeId);
        var tempPath = Path.Combine(contentDir, $".{nodeId}.learn.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(content, JsonOptions));
        File.Move(tempPath, finalPath, overwrite: true);
    }
}
