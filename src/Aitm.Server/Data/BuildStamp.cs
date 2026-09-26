using System.Security.Cryptography;

namespace Aitm.Server.Data;

/// <summary>
/// Decides whether the tool server must be rebuilt before launch. Ported verbatim from
/// build-stamp.mjs: launch-mcp.mjs built bin/ only when mcp.dll was missing. The plugin build lives
/// in CLAUDE_PLUGIN_DATA, which survives a plugin update, so an update never rebuilt it: a new tool
/// shipped in a release and a session on the updated plugin still ran the old server without it. The
/// stamp is a hash of the server's source; a different hash means the build is stale. Reported by the
/// server in <c>/health</c> (RESTRUCTURE.md slice 23).
/// </summary>
public static class BuildStamp
{
    private const string StampFileName = "build-stamp.txt";

    public static string SourceHash(string sourceFile)
    {
        using FileStream stream = File.OpenRead(sourceFile);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    public static bool NeedsBuild(string binDir, string sourceFile)
    {
        if (!File.Exists(Path.Combine(binDir, "mcp.dll"))) return true;
        if (!File.Exists(sourceFile)) return false; // nothing to rebuild from: run the build that exists
        string stampFile = Path.Combine(binDir, StampFileName);
        if (!File.Exists(stampFile)) return true;
        return File.ReadAllText(stampFile).Trim() != SourceHash(sourceFile);
    }

    public static void WriteStamp(string binDir, string sourceFile)
    {
        File.WriteAllText(Path.Combine(binDir, StampFileName), SourceHash(sourceFile));
    }

    /// <summary>
    /// The stamp of the published build this server runs from: <c>&lt;build&gt;/bin-cli/build-stamp.txt</c>
    /// beside <c>&lt;build&gt;/bin-server</c>, written by build-cli-and-server.mjs (RESTRUCTURE.md slice 32a).
    /// Read once at start, so a server keeps reporting its own build after <c>current</c> moves on (slice
    /// 32b). Null for a build with no stamp (a checkout's bin-server, a test build).
    /// </summary>
    public static string? OfPublishedBuild(string serverDirectory)
    {
        try
        {
            string stampFile = Path.Combine(serverDirectory, "..", "bin-cli", StampFileName);
            if (!File.Exists(stampFile)) return null;
            string stamp = File.ReadAllText(stampFile).Trim();
            return stamp.Length > 0 ? stamp : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
