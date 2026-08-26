using ApTutor.Content;

namespace ApTutor.ContentAdmin.Services;

/// StructureHash/StructureUpdatedAt point at the course's current curriculum structure (see
/// ApTutor.Curriculum.SkillDag and S3ContentStore.ApproveStructureAsync) — deliberately a single
/// pointer, not a growing list like NodeManifestEntry.Versions: a course has exactly one live DAG at
/// a time, there's no "rotate between several curricula" concept the way there is for practice-item
/// sets. Both nullable and additive to the existing shape, so manifests written before course
/// structure moved into S3 (every node manifest so far) still deserialize correctly with no
/// backward-compatibility shim needed — a missing JSON property just defaults to null.
public sealed record CourseManifest(
    int SchemaVersion,
    IReadOnlyDictionary<string, NodeManifestEntry> Nodes,
    string? StructureHash = null,
    DateTimeOffset? StructureUpdatedAt = null)
{
    public static CourseManifest Empty(int schemaVersion) =>
        new(schemaVersion, new Dictionary<string, NodeManifestEntry>(StringComparer.Ordinal));
}

public sealed record CourseIndexEntry(string ManifestHash, DateTimeOffset UpdatedAt);

public sealed record TopLevelManifest(int SchemaVersion, IReadOnlyDictionary<string, CourseIndexEntry> Courses)
{
    public static TopLevelManifest Empty(int schemaVersion) =>
        new(schemaVersion, new Dictionary<string, CourseIndexEntry>(StringComparer.Ordinal));
}

/// Pure merge logic, deliberately free of any S3/network I/O so it's unit-testable without mocking
/// anything. S3ContentStore is the only caller — it does the GET-merge-conditional-PUT-retry dance
/// around these functions.
public static class ManifestMerge
{
    /// Appends a newly-approved version rather than replacing what's there — a node accumulates a
    /// growing library of independently-approved sets over time (see the multi-set library plan),
    /// not a single "latest" pointer. Idempotent: re-approving a pack whose hash is already present
    /// (a harmless no-op re-approval, not a genuinely new set) doesn't add a duplicate entry.
    public static CourseManifest UpsertNode(CourseManifest current, string nodeId, string hash, DateTimeOffset updatedAt)
    {
        var existingVersions = current.Nodes.TryGetValue(nodeId, out var existing)
            ? existing.Versions
            : Array.Empty<NodeVersionEntry>();

        if (existingVersions.Any(v => v.Hash == hash))
            return current;

        var updatedVersions = existingVersions.Append(new NodeVersionEntry(hash, updatedAt)).ToList();
        var nodes = new Dictionary<string, NodeManifestEntry>(current.Nodes, StringComparer.Ordinal)
        {
            [nodeId] = new NodeManifestEntry(updatedVersions),
        };
        return current with { Nodes = nodes };
    }

    /// Overwrites the current structure pointer outright — unlike UpsertNode, there's no "keep the
    /// old one too" concept for course structure (see CourseManifest's remarks). Not idempotent on
    /// an unchanged hash the way UpsertNode is, since overwriting with the same value is already a
    /// no-op in effect.
    public static CourseManifest SetStructure(CourseManifest current, string hash, DateTimeOffset updatedAt) =>
        current with { StructureHash = hash, StructureUpdatedAt = updatedAt };

    public static TopLevelManifest UpsertCourse(TopLevelManifest current, string courseId, CourseIndexEntry entry)
    {
        var courses = new Dictionary<string, CourseIndexEntry>(current.Courses, StringComparer.Ordinal)
        {
            [courseId] = entry,
        };
        return current with { Courses = courses };
    }
}
