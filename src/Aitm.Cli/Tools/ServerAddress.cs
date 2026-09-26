using System.Security.Cryptography;
using System.Text;

namespace Aitm.Cli.Tools;

/// <summary>
/// RESTRUCTURE.md Slice P1: the service listens on a per-user, per-realm local pipe (Windows) or Unix
/// domain socket (macOS/Linux) instead of 127.0.0.1:7635. The "realm" is the data directory
/// (<c>AITM_DATA_DIR</c>, default <c>~/.aitm</c>); one pipe/socket per data directory, so two realms on
/// the same machine (or two OS users, each with their own default data directory) never collide.
///
/// Duplicated verbatim as <c>Aitm.Server.Data.ServerAddress</c>: Aitm.Cli and Aitm.Server sit at the same
/// reference level (ReferenceDirectionTests.EveryReferencePointsToALowerLevel), and Aitm.Cli references
/// nothing in Aitm at all (CliReferencesNothingInAitm), so neither project can depend on the other's copy.
/// ServerAddressMatchesAcrossCliAndServerTests is what would notice the two copies drifting apart.
/// </summary>
public static class ServerAddress
{
    /// <summary>The Windows named pipe name (without the <c>\\.\pipe\</c> prefix NamedPipeClientStream and
    /// Kestrel's ListenNamedPipe both add on their own) for the given data directory.</summary>
    public static string PipeName(string dataDir) =>
        "aitm-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(dataDir))))[..16].ToLowerInvariant();

    /// <summary>The one spelling of a data directory both sides hash: full path, no trailing separator, and
    /// upper-cased on Windows where paths are case-insensitive.</summary>
    private static string Normalize(string dataDir)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    /// <summary>The data directory ("realm"): <c>AITM_DATA_DIR</c>, else <c>~/.aitm</c>. An empty value counts
    /// as unset.</summary>
    public static string ResolveDataDir(string? configured, string userProfile) =>
        string.IsNullOrEmpty(configured) ? Path.Combine(userProfile, ".aitm") : configured;

    public static string ResolveDataDir() => ResolveDataDir(
        Environment.GetEnvironmentVariable("AITM_DATA_DIR"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The Unix domain socket path for the given data directory: a file inside it, so it inherits
    /// the data directory's own user-only permissions (mode 0700, set by whoever creates the directory).</summary>
    public static string SocketPath(string dataDir) => Path.Combine(dataDir, "server.sock");
}
