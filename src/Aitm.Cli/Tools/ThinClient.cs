using System.Net;
using System.Text;
using System.Text.Json;

namespace Aitm.Cli.Tools;

/// <summary>
/// RESTRUCTURE.md slice 29d, transport updated by Slice P1: `aitm &lt;verb&gt;` as a thin client of
/// Aitm.Server's <c>POST /cli</c>, reached over the local pipe / Unix socket (<see cref="PipeConnection"/>)
/// instead of <c>127.0.0.1:7635</c>. Body <c>{ "args", "cwd" }</c>, no token (AITM holds no secret; only
/// the current user can open the pipe), the project in the <c>Claude-Project-Dir</c> header (and
/// <c>Aitm-Instance</c> when set), exactly the inputs the old CLI resolved its instance from
/// (<c>AITM_INSTANCE</c>, <c>CLAUDE_PROJECT_DIR</c>, else the current directory). The answer's stdout and
/// stderr are written as they are and its <c>exitCode</c> is returned.
///
/// A server that refuses the connection is started once through <see cref="ServerAutoStart"/> and the call
/// is sent once more. Only a refused connection counts: any later failure may mean the verb already ran,
/// so it is never resent. Still unreachable: one stderr line, exit 1.
/// </summary>
public static class ThinClient
{
    /// <summary>Above the server's longest verb timeout (900 s), so the server's own 124 answer arrives first.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(960);

    /// <summary>A pipe/socket connect to a running server takes well under a millisecond; a server that is
    /// down never answers CreateFile at all, so this must stay short even though RequestTimeout is long
    /// (PipeConnection.ConnectAsync retries until its own timeout regardless of whether anything is listening).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public static int Run(string[] args, string cwd, string dataDir, string? instanceEnv, string? projectDirEnv,
        Func<bool> ensureServer, string serverExe, TextWriter stdout, TextWriter stderr)
    {
        using HttpClient client = PipeConnection.CreateClient(dataDir, ConnectTimeout, RequestTimeout);
        try
        {
            HttpResponseMessage response;
            try
            {
                response = Send(client, args, cwd, instanceEnv, projectDirEnv);
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
            {
                if (!ensureServer())
                {
                    string why = File.Exists(serverExe)
                        ? $"started {serverExe}, but /health did not answer within {ServerAutoStart.DefaultMaxWait.TotalSeconds:0} s"
                        : $"no server to start at {serverExe}";
                    stderr.WriteLine($"error: the aitm server for {dataDir} is not running ({why}).");
                    return 1;
                }
                response = Send(client, args, cwd, instanceEnv, projectDirEnv);
            }

            using (response)
            {
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    stderr.WriteLine($"error: the aitm server for {dataDir} answered {(int)response.StatusCode}.");
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
            stderr.WriteLine($"error: the aitm server for {dataDir} did not answer ({ex.GetType().Name}: {FirstLine(ex.Message)}).");
            return 1;
        }
    }

    /// <summary>The real client: data dir, env and server path as Aitm.Server and the SessionStart hook use them.</summary>
    public static int RunDefault(string[] args, TextWriter stdout, TextWriter stderr) => Run(
        args,
        Directory.GetCurrentDirectory(),
        ServerAutoStart.DefaultDataDir(),
        Environment.GetEnvironmentVariable("AITM_INSTANCE"),
        Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"),
        ServerAutoStart.EnsureRunning,
        ServerAutoStart.DefaultServerExe(),
        stdout,
        stderr);

    private static HttpResponseMessage Send(HttpClient client, string[] args, string cwd,
        string? instanceEnv, string? projectDirEnv)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/cli");
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
