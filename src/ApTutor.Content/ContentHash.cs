using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ApTutor.Content;

/// Shared between ApTutor.ContentAdmin (hashes a pack when writing it to S3) and ApTutor.Client
/// (recomputes the same hash locally to decide whether a cached node is already up to date) — the
/// diff only works if both sides serialize with byte-identical options, so this is the one place
/// that's allowed to define them. Not exposing the options for arbitrary reuse elsewhere; Compute
/// is the only thing callers should need.
public static class ContentHash
{
    public static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };

    public static string Compute(NodeContentPack pack) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pack, CanonicalOptions)))).ToLowerInvariant();
}
