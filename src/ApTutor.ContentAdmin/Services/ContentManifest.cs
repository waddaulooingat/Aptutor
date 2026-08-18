namespace ApTutor.ContentAdmin.Services;

public sealed record NodeManifestEntry(string Hash, DateTimeOffset UpdatedAt);

public sealed record CourseManifest(int SchemaVersion, IReadOnlyDictionary<string, NodeManifestEntry> Nodes)
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
    public static CourseManifest UpsertNode(CourseManifest current, string nodeId, NodeManifestEntry entry)
    {
        var nodes = new Dictionary<string, NodeManifestEntry>(current.Nodes, StringComparer.Ordinal)
        {
            [nodeId] = entry,
        };
        return current with { Nodes = nodes };
    }

    public static TopLevelManifest UpsertCourse(TopLevelManifest current, string courseId, CourseIndexEntry entry)
    {
        var courses = new Dictionary<string, CourseIndexEntry>(current.Courses, StringComparer.Ordinal)
        {
            [courseId] = entry,
        };
        return current with { Courses = courses };
    }
}
