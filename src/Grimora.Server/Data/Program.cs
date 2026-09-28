using System.Reflection;
using Grimora.Server.Data;
using Grimora.Store.Data;
using ModelContextProtocol.Server;

// The service is reached only through a local pipe (Windows) or Unix socket, current user only: no TCP, no
// token, no Host/Origin guard. Contract: docs/RESTRUCTURE.md, "Phase 4, replaced". One instance per data dir
// (ProcessOwner). An old server.token file is ignored, never deleted. /mcp: mcp.cs stays the live host until
// phase 3 switches over.

LegacyEnvironment.Promote();
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GRIMORA_DATA_DIR")))
    LegacyStore.MoveIfNeeded(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Console.Error);
string dataDir = ServerAddress.ResolveDataDir();
string projectRoot = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? Directory.GetCurrentDirectory();

Directory.CreateDirectory(dataDir);

// `using` keeps the lock handle alive to the end of the program, after the exit record is written. A plain local is
// dead after the null check, so a garbage collection (the stop runs one) would close it and free the lock early.
using FileStream? instanceLock = ProcessOwner.TryAcquireSingleInstanceLock(dataDir);
if (instanceLock is null)
{
    Console.Error.WriteLine(
        $"Grimora.Server is already running for {dataDir} (another process holds server.lock there). " +
        "Only one instance may run per data directory; stop it before starting another.");
    Environment.Exit(1);
    return;
}

// A record left by an earlier run must never be read as this run's (CleanExitRecord).
CleanExitRecord.DeleteStale(dataDir);
DateTime startedAt = DateTime.UtcNow;
string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "dev";
string? buildStamp = BuildStamp.OfPublishedBuild(AppContext.BaseDirectory);

string? unixSocketPath = OperatingSystem.IsWindows() ? null : ServerAddress.SocketPath(dataDir);

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
if (OperatingSystem.IsWindows())
{
    // Kestrel's named-pipe transport restricts the pipe to the current user by default; CurrentUserOnly
    // is set explicitly (not left as the default) so a future refactor cannot silently loosen it, and so
    // PipeOnlyTransportTests has something visible in source to pin.
    builder.WebHost.UseNamedPipes(options => options.CurrentUserOnly = true);
    builder.WebHost.ConfigureKestrel(o => o.ListenNamedPipe(ServerAddress.PipeName(dataDir)));
}
else
{
    // The socket file inherits the data directory's own permissions; the directory is set to user-only
    // (0700) below, and the socket file itself is set to 0600 right after Kestrel creates it (it does not
    // exist until the transport binds, so this cannot happen any earlier than after StartAsync).
    if (System.Text.Encoding.UTF8.GetByteCount(unixSocketPath!) > 100)
    {
        // sun_path holds about 104 bytes; a longer path fails in the kernel with a raw exception.
        Console.Error.WriteLine($"Grimora.Server: the socket path is too long ({unixSocketPath}); set GRIMORA_DATA_DIR to a shorter folder.");
        Environment.Exit(1);
        return;
    }
    try { File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    catch (Exception e) when (e is UnauthorizedAccessException or IOException)
    {
        // chmod fails when the folder belongs to another user: refuse rather than serve from a folder we cannot secure.
        Console.Error.WriteLine($"Grimora.Server: {dataDir} is not owned by this user, so its socket cannot be made user-only ({e.Message}).");
        Environment.Exit(1);
        return;
    }
    try { File.Delete(unixSocketPath!); } catch (IOException) { /* nothing to remove */ }
    builder.WebHost.ConfigureKestrel(o => o.ListenUnixSocket(unixSocketPath!));
}
// Configuration (Kestrel:Endpoints, ASPNETCORE_URLS, HTTP_PORTS) can add a TCP endpoint behind the code
// above. Refuse to start rather than open one: the service is reachable by the pipe/socket only.
string[] tcpConfigured =
[
    .. new[] { "urls", "HTTP_PORTS", "HTTPS_PORTS" }.Where(k => !string.IsNullOrWhiteSpace(builder.Configuration[k])),
    .. builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren()
        .Where(e => !string.IsNullOrWhiteSpace(e["Url"])).Select(e => "Kestrel:Endpoints:" + e.Key),
];
if (tcpConfigured.Length > 0)
{
    Console.Error.WriteLine(
        "Grimora.Server listens on its local pipe/socket only and refuses a configured TCP endpoint: " +
        string.Join(", ", tcpConfigured) + ". Remove it and start again.");
    Environment.Exit(1);
    return;
}
// RESTRUCTURE.md "Slice 32b": a stop (POST /shutdown, Ctrl+C, a logoff) lets every call in flight finish.
// The longest call the server takes is a long /cli verb, so the drain waits that long, never the 30 s default.
// The host's own limit sits 30 s beyond it, so a drain that ran past LongTimeout is seen as such (not clean) before
// the host cuts the calls off.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = CliEndpoint.LongTimeout + TimeSpan.FromSeconds(30));

// The one open store per project (RESTRUCTURE.md "Slice 26") and the accessor every store-backed tool
// uses to resolve which project a request belongs to. Both are constructed here, not resolved from the
// container later, so McpToolFactory can close over the exact instances the framework will populate
// per request (Kestrel hosting sets the registered IHttpContextAccessor's HttpContext itself).
ProjectStore projectStore = new(dataDir);
Microsoft.AspNetCore.Http.HttpContextAccessor httpContextAccessor = new();
builder.Services.AddSingleton(projectStore);
builder.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(httpContextAccessor);

IReadOnlyList<McpServerTool> mcpTools = McpToolFactory.BuildTools(
    AllMcpTools.BuildRegistry(), projectStore, httpContextAccessor, projectRoot);
builder.Services.AddMcpServer().WithHttpTransport().WithTools(mcpTools);
// RESTRUCTURE.md Slice P1: the same tools as plain functions, for /tools (what `grimora mcp` forwards to).
IReadOnlyList<Microsoft.Extensions.AI.AIFunction> toolFunctions = McpToolFactory.BuildFunctions(
    AllMcpTools.BuildRegistry(), projectStore, httpContextAccessor, projectRoot);

WebApplication app = builder.Build();

// On demand: the service exits when it has had no real call for the idle time. /health does not count.
TimeSpan idleTime = IdleExit.ConfiguredIdle();
IdleExit idleExit = new(idleTime, () => app.Lifetime.StopApplication());

// RESTRUCTURE.md "Slice 38": SessionEnd's indexing runs off the request thread (HookEndpoint), so its
// own HTTP answer stays inside the 1.5 s hooks.json budget. It shares idleExit's own "in flight" count
// (IndexJobQueue.Enqueue takes a token before this returns), so a shutdown drain or an idle exit waits
// for a queued job the same way it already waits for a call still in flight, instead of losing it.
IndexJobQueue indexQueue = new(projectStore, idleExit);
// Never awaited: this loop only ends when the process does. A bug inside it must never take a later
// SessionEnd down with it (RunAsync's own try/catch per job already keeps one job's failure from
// stopping the next), so nothing here needs to observe its completion.
_ = indexQueue.RunAsync();

// Stopped, not Stopping: Stopping fires before Kestrel drains, so a call still in flight would lose its store
// (RESTRUCTURE.md slice 32b: the old server finishes its calls in flight, then exits). A /cli verb that outlived
// its timeout runs on its own thread with no request behind it, so the drain waits for it too.
// Every stop leaves CleanExitRecord before the process frees server.lock: the ids of the calls this run started
// and whether it drained them within LongTimeout. If a connection closes before its request is read, the
// record can prove that call never ran, so the client resends it (RefusedConnectionRetry); any other lost call
// is reported, never repeated. The stop closing an accepted but unread connection is proven on Windows only;
// Unix sockets may serve a request on an existing connection during graceful shutdown (issue #4).
CallRing startedCalls = new();
System.Diagnostics.Stopwatch stopClock = new();
app.Lifetime.ApplicationStopping.Register(stopClock.Start);
app.Lifetime.ApplicationStopped.Register(() =>
{
    TimeSpan left = CliEndpoint.LongTimeout - stopClock.Elapsed;
    bool clean = left > TimeSpan.Zero && idleExit.WaitForDrain(left);
    projectStore.Dispose();
    (IReadOnlyList<string> ids, bool wrapped, DateTime oldestKept) = startedCalls.Snapshot();
    try { new CleanExitRecord(startedAt, DateTime.UtcNow, clean, wrapped, oldestKept, ids).Write(dataDir); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* no record: a lost call is not resent */ }
});
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health") { await next(context); return; }
    // Recorded before any work, so a call that may have run is always in the ring (CleanExitRecord).
    string? callId = context.Request.Headers["X-Grimora-Call"];
    if (!string.IsNullOrEmpty(callId)) startedCalls.Record(callId);
    using (idleExit.Begin()) await next(context);
});
TimeSpan idleCheckInterval = TimeSpan.FromSeconds(1) < idleTime / 4 ? TimeSpan.FromSeconds(1) : idleTime / 4;
using Timer idleTimer = new(_ => idleExit.CheckAndStopIfIdle(), null, idleCheckInterval, idleCheckInterval);

app.MapGet("/health", () => Results.Json(new
{
    pid = Environment.ProcessId,
    idleExitSeconds = (int)idleTime.TotalSeconds,
    version,
    buildStamp,
    uptimeSeconds = (DateTime.UtcNow - startedAt).TotalSeconds,
    openStores = projectStore.OpenInstances,
}));

// The 24 golden MCP tools. Reachable only over the pipe/socket above (no Host/Origin guard needed).
app.MapMcp("/mcp");

// RESTRUCTURE.md Slice P1: the tool list and one tool call for `grimora mcp` (ToolsEndpoint); same project
// resolution and writer gate as /mcp.
app.MapGet("/tools", () => ToolsEndpoint.List(toolFunctions));
app.MapPost("/tools/{name}", (string name, HttpContext context) => ToolsEndpoint.Call(name, context, toolFunctions));

// RESTRUCTURE.md "Slice 34": Claude Code http hooks; runs the slice 20-22 handlers under the same
// per-project writer gate as /mcp (HookEndpoint).
app.MapPost("/hooks/{event}", (string @event, HttpContext context) => HookEndpoint.Handle(@event, context, projectStore, indexQueue));

// RESTRUCTURE.md "Slice 29c": the CLI verbs; each runs on the project's one open connection under its
// writer gate, with a timeout that answers exit 124 (CliEndpoint).
app.MapPost("/cli", (Func<HttpContext, Task<IResult>>)(context => CliEndpoint.Handle(context, projectStore, dataDir, idleExit)));

// RESTRUCTURE.md "Slice 32b": the SessionStart of a newer build asks this server to make way. It stops
// taking new connections, finishes the calls in flight, and exits; that frees server.lock for the new build.
app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.Accepted();
});

await app.StartAsync();
// Kestrel reports a named pipe as http://pipe:/name and a Unix socket as http://unix:/path; those are ours.
string[] tcpBound = [.. app.Urls.Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)
    && !u.StartsWith("http://pipe:", StringComparison.OrdinalIgnoreCase)
    && !u.StartsWith("http://unix:", StringComparison.OrdinalIgnoreCase))];
if (tcpBound.Length > 0)
{
    Console.Error.WriteLine("Grimora.Server bound a TCP address (" + string.Join(", ", tcpBound) + "); stopping.");
    await app.StopAsync();
    Environment.Exit(1);
    return;
}
if (!OperatingSystem.IsWindows() && unixSocketPath is not null)
{
    // The socket file only exists from here on: Kestrel creates it when the transport binds, which
    // happens inside StartAsync, not at ListenUnixSocket configuration time.
    try { File.SetUnixFileMode(unixSocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    catch (IOException) { /* the socket file vanished (a racing shutdown); nothing left to secure */ }
}
await app.WaitForShutdownAsync();

// Makes the top-level Program class visible to Grimora.Server.Tests' WebApplicationFactory<Program>.
public partial class Program;
