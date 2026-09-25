using System.Text.Json;

namespace Aitm.Cli.Tools;

/// <summary>
/// `aitm server headers` (RESTRUCTURE.md slice 28): the `.mcp.json` headersHelper. Claude Code runs it on
/// each connection and reconnect, without credential-like env vars, so it reads the bearer token from the
/// server token file and prints <c>{"Authorization":"Bearer &lt;token&gt;"}</c>. The file is the one
/// Aitm.Server's ServerToken writes: <c>server.token</c> in <c>AITM_DATA_DIR</c>, else <c>~/.aitm</c>
/// (Aitm.Server's Program.cs). Aitm.Cli references nothing in AITM (ReferenceDirectionTests), so this is
/// its own small reader. No token, or an unreadable one, prints <c>{}</c>: Claude Code then connects
/// without auth, gets a 401 and retries with a fresh helper run. The token goes to stdout only, never to
/// stderr or a log.
/// </summary>
public static class ServerHeadersCommand
{
    public static int Run(string dataDir, TextWriter stdout)
    {
        string token = "";
        try
        {
            string path = Path.Combine(dataDir, "server.token");
            if (File.Exists(path)) token = File.ReadAllText(path).Trim();
        }
        catch
        {
            // An unreadable file is the same as no token; say nothing that could carry it.
            token = "";
        }

        if (token.Length == 0)
        {
            stdout.WriteLine("{}");
            return 0;
        }

        stdout.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }));
        return 0;
    }

    /// <summary>The data dir Aitm.Server uses: <c>AITM_DATA_DIR</c>, else <c>~/.aitm</c>.</summary>
    public static string DefaultDataDir() =>
        Environment.GetEnvironmentVariable("AITM_DATA_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm");
}
