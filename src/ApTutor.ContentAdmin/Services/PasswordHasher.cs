using System.Security.Cryptography;

namespace ApTutor.ContentAdmin.Services;

/// PBKDF2 password hashing with zero extra NuGet dependency (System.Security.Cryptography only),
/// matching this repo's existing convention. There's exactly one admin credential (the hired SME),
/// sourced from an environment variable — see Program.cs — never checked into appsettings.json.
public static class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// Stored format: "{iterations}.{saltBase64}.{hashBase64}" — self-describing so the iteration
    /// count can be raised later without breaking existing stored hashes.
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.', 3);
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations)) return false;

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
