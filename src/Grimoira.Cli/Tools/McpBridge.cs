using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Grimoira.Cli.Tools;

/// <summary>
/// RESTRUCTURE.md Slice P1: `grimoira mcp`, Claude Code's MCP server over stdio. It holds no tool of its own: on
/// start it reads the tool list from the service (<c>GET /tools</c>, over the pipe of <see cref="PipeConnection"/>,
/// starting the service through <see cref="ServerAutoStart"/> when it is down) and serves exactly that list;
/// each call is forwarded to <c>POST /tools/{name}</c> with the <c>Claude-Project-Dir</c> header (and
/// <c>Grimoira-Instance</c> when GRIMOIRA_INSTANCE is set), so the service resolves the project and stays the one writer.
///
/// stdout is the protocol stream: this class writes nothing to it except through the SDK's stdio transport.
/// Diagnostics go to stderr. A failed call is an MCP tool error (<c>isError</c>), never a crash.
/// </summary>
public static class McpBridge
{
    /// <summary>Above the service's longest tool time, so the service's own answer arrives first.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(960);

    /// <summary>
    /// This bridge process's own session identity, sent as the <c>Grimoira-Session</c> header on every
    /// forwarded call so the service can key a staged learning's ledger by session
    /// (<c>Grimoira.Server.Data.RequestSessionResolver</c>, <c>BrainStageTool.LedgerPath</c>) instead of one
    /// ledger shared by every session on the instance. `grimoira mcp` runs once per Claude Code session (one
    /// process, stdio-piped for the session's lifetime), so a GUID minted once here at process start is
    /// already the right scope — no session id needs to come from Claude Code itself. CLAUDE_SESSION_ID is
    /// preferred when Claude Code does set it, so a hook (which gets the same id in its payload) and this
    /// bridge agree on one session's identity instead of each minting its own.
    /// </summary>
    private static readonly string SessionId =
        Environment.GetEnvironmentVariable("CLAUDE_SESSION_ID") is { Length: > 0 } fromEnv
            ? fromEnv
            : Guid.NewGuid().ToString("N");

    /// <summary>The real client: data dir, env and server path as Grimoira.Server and the hooks use them. The
    /// process streams are passed in by the entry point (Program.cs), the one file allowed to name Console.</summary>
    public static int RunDefault(Stream stdin, Stream stdout, TextWriter stderr) => Run(
        ServerAddress.ResolveDataDir(),
        Directory.GetCurrentDirectory(),
        Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE"),
        Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"),
        ServerAutoStart.EnsureRunning,
        ServerAutoStart.DefaultServerExe(),
        stdin,
        stdout,
        stderr);

    public static int Run(string dataDir, string cwd, string? instanceEnv, string? projectDirEnv,
        Func<bool> ensureServer, string serverExe, Stream stdin, Stream stdout, TextWriter stderr)
    {
        using HttpClient client = PipeConnection.CreateClient(dataDir, ThinClient.ConnectTimeout, RequestTimeout);
        string projectDir = string.IsNullOrWhiteSpace(projectDirEnv) ? cwd : projectDirEnv;

        List<Tool> tools;
        try
        {
            tools = FetchTools(client, dataDir, ensureServer, out string? failure);
            if (failure is not null)
            {
                string why = File.Exists(serverExe)
                    ? $"started {serverExe}, but it did not answer within {ServerAutoStart.DefaultMaxWait.TotalSeconds:0} s"
                    : $"no server to start at {serverExe}";
                stderr.WriteLine($"error: grimoira mcp: the grimoira server for {dataDir} is not running ({why}).");
                return 1;
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: grimoira mcp: could not read the tool list from the grimoira server for {dataDir} ({ex.GetType().Name}: {FirstLine(ex.Message)}).");
            return 1;
        }

        McpServerOptions options = new()
        {
            ServerInfo = new Implementation { Name = "grimoira", Version = "2.2.3" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => new ValueTask<ListToolsResult>(new ListToolsResult { Tools = tools }),
                CallToolHandler = async (context, cancellationToken) =>
                {
                    string name = context.Params?.Name ?? "";
                    return await CallAsync(client, dataDir, ensureServer, name, context.Params?.Arguments, projectDir, instanceEnv, cancellationToken);
                },
            },
        };

        try
        {
            Serve(options, stdin, stdout).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: grimoira mcp: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            return 1;
        }
    }

    private static async Task Serve(McpServerOptions options, Stream stdin, Stream stdout)
    {
        await using StreamServerTransport transport = new(stdin, stdout, "grimoira");
        await using McpServer server = McpServer.Create(transport, options);
        await server.RunAsync();
    }

    /// <summary>The service's tool list; <paramref name="failure"/> is set when it never answered, even after a start.</summary>
    private static List<Tool> FetchTools(HttpClient client, string dataDir, Func<bool> ensureServer, out string? failure)
    {
        failure = null;
        LostCallGuard guard = new(dataDir, RefusedConnectionRetry.DefaultWaitBudget);
        HttpResponseMessage List()
        {
            using HttpRequestMessage request = new(HttpMethod.Get, "/tools");
            request.Headers.TryAddWithoutValidation(RefusedConnectionRetry.CallIdHeader, guard.CallId);
            return client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        }
        if (!RefusedConnectionRetry.TrySend(List, ensureServer, out HttpResponseMessage? response, guard))
        {
            failure = "unreachable";
            return [];
        }

        string body;
        using (response) body = response!.EnsureSuccessStatusCode().Content.ReadAsStringAsync().GetAwaiter().GetResult();

        using JsonDocument doc = JsonDocument.Parse(body);
        return [.. doc.RootElement.EnumerateArray().Select(t => new Tool
        {
            Name = t.GetProperty("name").GetString()!,
            Description = t.GetProperty("description").GetString(),
            InputSchema = t.GetProperty("inputSchema").Clone(),
        })];
    }

    private static async Task<CallToolResult> CallAsync(HttpClient client, string dataDir, Func<bool> ensureServer, string name,
        IDictionary<string, JsonElement>? arguments, string projectDir, string? instanceEnv, CancellationToken cancellationToken)
    {
        try
        {
            // The service went away since the last call (an idle exit, a hand-over): start it and resend, up to
            // 3 times (RefusedConnectionRetry). A refused connection is resent; any later failure only when the
            // exiting service proved it never started the tool (its clean-exit record, by call id).
            LostCallGuard guard = new(dataDir, RefusedConnectionRetry.DefaultWaitBudget);
            (bool reached, HttpResponseMessage? response) = await RefusedConnectionRetry.TrySendAsync(
                () => SendAsync(client, name, arguments, projectDir, instanceEnv, guard.CallId, cancellationToken), ensureServer, cancellationToken, guard);
            if (!reached) return Error("the grimoira server is not running and could not be started");

            using (response)
            {
                string text = await response!.Content.ReadAsStringAsync(cancellationToken);
                return response.StatusCode == HttpStatusCode.OK
                    ? new CallToolResult { Content = [new TextContentBlock { Text = text }] }
                    : Error(text.Length > 0 ? text : $"the grimoira server answered {(int)response.StatusCode}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error($"{name} failed ({ex.GetType().Name}: {FirstLine(ex.Message)})");
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string name, IDictionary<string, JsonElement>? arguments,
        string projectDir, string? instanceEnv, string callId, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"/tools/{Uri.EscapeDataString(name)}");
        request.Headers.TryAddWithoutValidation(RefusedConnectionRetry.CallIdHeader, callId);
        request.Headers.TryAddWithoutValidation("Claude-Project-Dir", projectDir);
        if (!string.IsNullOrWhiteSpace(instanceEnv)) request.Headers.TryAddWithoutValidation("Grimoira-Instance", instanceEnv);
        request.Headers.TryAddWithoutValidation("Grimoira-Session", SessionId);
        request.Content = new StringContent(JsonSerializer.Serialize(arguments ?? new Dictionary<string, JsonElement>()), Encoding.UTF8, "application/json");
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static string FirstLine(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        return end >= 0 ? text[..end] : text;
    }
}
