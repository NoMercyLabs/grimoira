using System.Security.Cryptography;

namespace Aitm.Server.Data;

/// <summary>
/// The bearer token every route but <c>/health</c> requires (RESTRUCTURE.md slice 25: "the token is
/// created once in the AITM data dir as server.token with user-only access [...] Unix 0600 at creation;
/// Windows ACL with only the current user, inheritance removed"), written through
/// <see cref="UserOnlySecretFile"/>.
/// </summary>
public static class ServerToken
{
    public const string FileName = "server.token";

    /// <summary>Creates the token once and reuses it on every later call (a restart never rotates it).</summary>
    public static string EnsureToken(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        string path = Path.Combine(dataDir, FileName);
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0) return existing;
        }

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        UserOnlySecretFile.Write(path, stream =>
        {
            using StreamWriter writer = new(stream);
            writer.Write(token);
        });
        return token;
    }
}
