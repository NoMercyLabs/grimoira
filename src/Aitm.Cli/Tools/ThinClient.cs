using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Aitm.Cli.Tools;

/// <summary>
/// RESTRUCTURE.md slice 29d: `aitm &lt;verb&gt;` as a thin client of Aitm.Server's <c>POST /cli</c>. Body
/// <c>{ "args", "cwd" }</c>, bearer token from <c>server.token</c> (the file `aitm server headers` reads),
/// the project in the <c>Claude-Project-Dir</c> header (and <c>Aitm-Instance</c> when set), exactly the inputs
/// the old CLI resolved its instance from (<c>AITM_INSTANCE</c>, <c>CLAUDE_PROJECT_DIR</c>, else the current
/// directory). The answer's stdout and stderr are written as they are and its <c>exitCode</c> is returned.
///
/// A server that refuses the connection is started once through <see cref="ServerAutoStart"/> and the call
/// is sent once more. Only a refused connection counts: any later failure may mean the verb already ran,
/// so it is never resent. Still unreachable: one stderr line, exit 1.
/// </summary>
public static class ThinClient
{
    /// <summary>Above the server's longest verb timeout (900 s), so the server's own 124 answer arrives first.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(960);

    public static int Run(string[] args, string cwd, int port, string dataDir, string? instanceEnv, string? projectDirEnv,
        Func<bool> ensureServer, string serverExe, TextWriter stdout, TextWriter stderr)
    {
        string url = $"http://127.0.0.1:{port}/cli";
        using HttpClient client = new() { Timeout = RequestTimeout };
        try
        {
            HttpResponseMessage response;
            try
            {
                response = Send(client, url, args, cwd, dataDir, instanceEnv, projectDirEnv);
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
            {
                if (!ensureServer())
                {
                    string why = File.Exists(serverExe)
                        ? $"started {serverExe}, but /health did not answer within {ServerAutoStart.DefaultMaxWait.TotalSeconds:0} s"
                        : $"no server to start at {serverExe}";
                    stderr.WriteLine($"error: the aitm server at http://127.0.0.1:{port} is not running ({why}).");
                    return 1;
                }
                response = Send(client, url, args, cwd, dataDir, instanceEnv, projectDirEnv);
            }

            using (response)
            {
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    string hint = response.StatusCode == HttpStatusCode.Unauthorized ? "; the token in server.token was refused" : "";
                    stderr.WriteLine($"error: the aitm server at http://127.0.0.1:{port} answered {(int)response.StatusCode}{hint}.");
                    return 1;
                }
                using JsonDocument doc = JsonDocument.Parse(body);
                stdout.Write(doc.RootElement.GetProperty("stdout").GetString());
                stderr.Write(doc.RootElement.GetProperty("stderr").GetString());
                return doc.RootElement.GetProperty("exitCode").GetInt32();
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: the aitm server at http://127.0.0.1:{port} did not answer ({ex.GetType().Name}: {FirstLine(ex.Message)}).");
            return 1;
        }
    }

    /// <summary>The real client: port, data dir, env and server path as Aitm.Server and the SessionStart hook use them.</summary>
    public static int RunDefault(string[] args, TextWriter stdout, TextWriter stderr) => Run(
        args,
        Directory.GetCurrentDirectory(),
        ServerAutoStart.DefaultPort(),
        ServerHeadersCommand.DefaultDataDir(),
        Environment.GetEnvironmentVariable("AITM_INSTANCE"),
        Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"),
        ServerAutoStart.EnsureRunning,
        ServerAutoStart.DefaultServerExe(),
        stdout,
        stderr);

    private static HttpResponseMessage Send(HttpClient client, string url, string[] args, string cwd, string dataDir,
        string? instanceEnv, string? projectDirEnv)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url);
        // Read on each send: a server started on demand writes server.token as it starts.
        string token = ServerHeadersCommand.ReadToken(dataDir);
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Claude-Project-Dir", string.IsNullOrWhiteSpace(projectDirEnv) ? cwd : projectDirEnv);
        if (!string.IsNullOrWhiteSpace(instanceEnv)) request.Headers.TryAddWithoutValidation("Aitm-Instance", instanceEnv);
        request.Content = new StringContent(JsonSerializer.Serialize(new { args, cwd }), Encoding.UTF8, "application/json");
        return client.Send(request);
    }

    private static string FirstLine(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        return end >= 0 ? text[..end] : text;
    }
}
