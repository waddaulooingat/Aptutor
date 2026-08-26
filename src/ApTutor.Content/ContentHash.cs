using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApTutor.Content;

/// Shared between ApTutor.ContentAdmin (hashes a pack when writing it to S3) and ApTutor.Client
/// (recomputes the same hash locally to decide whether a cached node is already up to date) — the
/// diff only works if both sides serialize with byte-identical options, so this is the one place
/// that's allowed to define them. Not exposing the options for arbitrary reuse elsewhere; Compute
/// is the only thing callers should need.
///
/// Generic rather than NodeContentPack-specific so the same content-addressing scheme covers course
/// structure (ApTutor.Curriculum.SkillDag) too — see the Shell-display-only/course-authoring plan.
/// This project deliberately has no reference to ApTutor.Curriculum; a generic method needs no such
/// reference to hash a SkillDag, whereas a SkillDag-specific overload would.
public static class ContentHash
{
    // The camelCase string enum converter matters specifically for SkillDag.DagNode.Type
    // (NodeType) — without it, System.Text.Json's default enum handling writes the *numeric*
    // value, which round-trips fine on both sides but produces a structure.json in S3 that doesn't
    // match the human-readable "concept"/"skill"/... convention every hand-authored DAG file
    // already uses (see SkillDagLoader's own options). No NodeContentPack-reachable type has any
    // enum field today, so this can't change any hash already computed for approved node content.
    public static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Compute<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, CanonicalOptions)))).ToLowerInvariant();
}
