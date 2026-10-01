using System.Security.Cryptography;
using System.Text;

namespace Grimoira.Cli.Tools;

/// <summary>
/// RESTRUCTURE.md Slice P1: the service listens on a per-user, per-realm local pipe (Windows) or Unix
/// domain socket (macOS/Linux) instead of 127.0.0.1:7635. The "realm" is the data directory
/// (<c>GRIMOIRA_DATA_DIR</c>, default <c>~/.grimoira</c>); one pipe/socket per data directory, so two realms on
/// the same machine (or two OS users, each with their own default data directory) never collide.
///
/// Duplicated verbatim as <c>Grimoira.Server.Data.ServerAddress</c>: Grimoira.Cli and Grimoira.Server sit at the same
/// reference level (ReferenceDirectionTests.EveryReferencePointsToALowerLevel), and Grimoira.Cli references
/// nothing in Grimoira at all (CliReferencesNothingInGrimoira), so neither project can depend on the other's copy.
/// ServerAddressMatchesAcrossCliAndServerTests is what would notice the two copies drifting apart.
/// </summary>
public static class ServerAddress
{
    /// <summary>The Windows named pipe name (without the <c>\\.\pipe\</c> prefix NamedPipeClientStream and
    /// Kestrel's ListenNamedPipe both add on their own) for the given data directory.</summary>
    public static string PipeName(string dataDir) =>
        "grimoira-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(dataDir))))[..16].ToLowerInvariant();

    /// <summary>The one spelling of a data directory both sides hash: full path, no trailing separator, and
    /// upper-cased on Windows where paths are case-insensitive.</summary>
    private static string Normalize(string dataDir)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    /// <summary>The data directory ("realm"): <c>GRIMOIRA_DATA_DIR</c>, else <c>~/.grimoira</c>. An empty value counts
    /// as unset.</summary>
    public static string ResolveDataDir(string? configured, string userProfile) =>
        string.IsNullOrEmpty(configured) ? Path.Combine(userProfile, ".grimoira") : configured;

    public static string ResolveDataDir() => ResolveDataDir(
        Environment.GetEnvironmentVariable("GRIMOIRA_DATA_DIR"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The Unix domain socket path for the given data directory: a file inside it, so it inherits
    /// the data directory's own user-only permissions (mode 0700, set by whoever creates the directory).</summary>
    /// <summary>The service's idle exit when nothing configures one: the server's default and what
    /// `grimoira service status` assumes of a server that does not report it.</summary>
    public const int DefaultIdleMinutes = 30;

    public static string SocketPath(string dataDir) => Path.Combine(dataDir, "server.sock");
}
