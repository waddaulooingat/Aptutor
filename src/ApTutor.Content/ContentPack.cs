// Advanced Test Prepper — Phase 7: the content-pack schema. This is the shared contract between
// the build-time factory (ApTutor.ContentFactory, which writes these) and the runtime readers
// below (which any course's ICourseModule can use to serve verified content — course-agnostic,
// no CS A-specific code here). One file per DAG node, named "<nodeId>.json".

using System.Text.Json;
using ApTutor.Platform;
using ApTutor.Scene;

namespace ApTutor.Content;

/// Everything generated for one DAG node: the practice items, the intro/explanation text, and the
/// authored animated walkthrough (a SceneDelta step stream, playable through the same scrubbing/
/// TTS UI as the CS A tracer's output). Unverified until a human reviews it (see Reviewer in
/// ApTutor.ContentFactory) — Verified gates whether the runtime readers below will serve it at all,
/// so a paid product can never accidentally ship a wrong explanation.
public sealed record NodeContentPack(
    string CourseId,
    string NodeId,
    string ExampleId,
    string WalkthroughText,
    IReadOnlyList<PracticeItem> PracticeItems,
    IReadOnlyList<VisualStep> WalkthroughSteps,
    bool Verified,
    DateTimeOffset GeneratedAt,
    string Model);

public static class ContentPackStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string PathFor(string contentDir, string nodeId) => Path.Combine(contentDir, $"{nodeId}.json");

    public static NodeContentPack? TryLoad(string contentDir, string nodeId)
    {
        var path = PathFor(contentDir, nodeId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<NodeContentPack>(File.ReadAllText(path), JsonOptions)
            : null;
    }

    /// Writes via a temp file + File.Move(overwrite:true) rather than a direct WriteAllText — a
    /// crash mid-write (container OOM-kill, forced redeploy) must never leave a truncated JSON file
    /// behind, since one corrupt file used to take down all of LoadAll (see below).
    public static void Save(string contentDir, NodeContentPack pack)
    {
        Directory.CreateDirectory(contentDir);
        var finalPath = PathFor(contentDir, pack.NodeId);
        var tempPath = Path.Combine(contentDir, $".{pack.NodeId}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(pack, JsonOptions));
        File.Move(tempPath, finalPath, overwrite: true);
    }

    public static void Delete(string contentDir, string nodeId)
    {
        var path = PathFor(contentDir, nodeId);
        if (File.Exists(path)) File.Delete(path);
    }

    /// All content packs in a directory, one per "<nodeId>.json" file — pending and verified alike;
    /// callers (like Reviewer) filter on .Verified themselves. A single malformed file (partial
    /// write, hand-edited JSON) is skipped rather than throwing, so it can't take down the entire
    /// pending-review listing.
    public static IReadOnlyList<NodeContentPack> LoadAll(string contentDir)
    {
        if (!Directory.Exists(contentDir)) return Array.Empty<NodeContentPack>();

        return Directory.EnumerateFiles(contentDir, "*.json")
            .Select(path =>
            {
                try
                {
                    return JsonSerializer.Deserialize<NodeContentPack>(File.ReadAllText(path), JsonOptions);
                }
                catch (JsonException)
                {
                    return null;
                }
            })
            .Where(pack => pack != null)
            .Select(pack => pack!)
            .OrderBy(pack => pack.NodeId, StringComparer.Ordinal)
            .ToList();
    }
}

/// Serves verified practice items + walkthrough text from a content-pack directory. Course-agnostic
/// — any ICourseModule can point this at its own content directory.
public sealed class FileContentSource : IContentSource
{
    private readonly string _contentDir;

    public FileContentSource(string contentDir) => _contentDir = contentDir;

    public string GetWalkthroughText(string nodeId, string exampleId) =>
        ContentPackStore.TryLoad(_contentDir, nodeId) is { Verified: true } pack
            ? pack.WalkthroughText
            : throw new InvalidOperationException($"No verified walkthrough text for node '{nodeId}'.");

    public IReadOnlyList<PracticeItem> GetPracticeItems(string nodeId) =>
        ContentPackStore.TryLoad(_contentDir, nodeId) is { Verified: true } pack
            ? pack.PracticeItems
            : Array.Empty<PracticeItem>();
}

/// Serves verified authored walkthroughs (SceneDelta step streams) from a content-pack directory.
/// SupportsLiveInput is always false — authored content replays a fixed animation, it doesn't trace
/// arbitrary student code (that's the CS A tracer's job, not this one's).
public sealed class AuthoredStepProvider : IStepProvider
{
    private readonly string _contentDir;

    public AuthoredStepProvider(string contentDir) => _contentDir = contentDir;

    public bool SupportsLiveInput => false;

    public IReadOnlyList<VisualStep> GetSteps(string nodeId, string exampleId) =>
        ContentPackStore.TryLoad(_contentDir, nodeId) is { Verified: true } pack
            ? pack.WalkthroughSteps
            : throw new InvalidOperationException($"No verified authored walkthrough for node '{nodeId}'.");
}
