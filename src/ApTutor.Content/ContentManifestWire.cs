using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApTutor.Content;

public sealed record NodeVersionEntry(string Hash, DateTimeOffset UpdatedAt);

/// One node's approved content, as one or more independently-generated, independently-approved
/// versions — a node accumulates a growing library of sets over time (see the multi-set library
/// plan) rather than a single "latest" pointer. Shared between ApTutor.ContentAdmin (which writes
/// this, appending on every Approve) and ApTutor.Client (which reads it to know what to sync down)
/// so both sides parse the exact same wire shape, including the backward-compatibility handling
/// below — duplicating that logic on both sides would risk the two silently drifting.
[JsonConverter(typeof(NodeManifestEntryConverter))]
public sealed record NodeManifestEntry(IReadOnlyList<NodeVersionEntry> Versions)
{
    /// The most recently-approved version — used wherever only one representative pack is needed
    /// (e.g. Content Admin's Review page showing "what's currently live" for context), as opposed to
    /// the Shell, which downloads and rotates among every version.
    public NodeVersionEntry Latest => Versions.OrderBy(v => v.UpdatedAt).Last();
}

/// Reads either the current shape (<c>{"Versions": [{"Hash": ..., "UpdatedAt": ...}, ...]}</c>) or
/// the single-version shape every manifest used before multi-set support existed
/// (<c>{"Hash": ..., "UpdatedAt": ...}</c>) — real approved content already sits in S3 under the old
/// shape, and there's no one-time migration step; a node written under the old shape simply upgrades
/// in place the next time it's approved again. Always writes the current shape.
public sealed class NodeManifestEntryConverter : JsonConverter<NodeManifestEntry>
{
    public override NodeManifestEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.TryGetProperty("Versions", out var versionsEl))
        {
            var versions = versionsEl.Deserialize<List<NodeVersionEntry>>(options) ?? new List<NodeVersionEntry>();
            return new NodeManifestEntry(versions);
        }

        // Old, single-version shape.
        var hash = root.GetProperty("Hash").GetString()!;
        var updatedAt = root.GetProperty("UpdatedAt").GetDateTimeOffset();
        return new NodeManifestEntry(new[] { new NodeVersionEntry(hash, updatedAt) });
    }

    public override void Write(Utf8JsonWriter writer, NodeManifestEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Versions");
        JsonSerializer.Serialize(writer, value.Versions, options);
        writer.WriteEndObject();
    }
}
