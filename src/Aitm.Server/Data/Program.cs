using System.Reflection;
using Aitm.Server.Data;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using ModelContextProtocol.Server;

// The service is reached only through a local pipe (Windows) or Unix socket, current user only: no TCP, no
// token, no Host/Origin guard. Contract: docs/RESTRUCTURE.md, "Phase 4, replaced". One instance per data dir
// (ProcessOwner). An old server.token file is ignored, never deleted. /mcp: mcp.cs stays the live host until
// phase 3 switches over.

string dataDir = ServerAddress.ResolveDataDir();
string projectRoot = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? Directory.GetCurrentDirectory();

Directory.CreateDirectory(dataDir);

FileStream? instanceLock = ProcessOwner.TryAcquireSingleInstanceLock(dataDir);
if (instanceLock is null)
{
    Console.Error.WriteLine(
        $"Aitm.Server is already running for {dataDir} (another process holds server.lock there). " +
        "Only one instance may run per data directory; stop it before starting another.");
    Environment.Exit(1);
    return;
}

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
        Console.Error.WriteLine($"Aitm.Server: the socket path is too long ({unixSocketPath}); set AITM_DATA_DIR to a shorter folder.");
        Environment.Exit(1);
        return;
    }
    try { File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    catch (Exception e) when (e is UnauthorizedAccessException or IOException)
    {
        // chmod fails when the folder belongs to another user: refuse rather than serve from a folder we cannot secure.
        Console.Error.WriteLine($"Aitm.Server: {dataDir} is not owned by this user, so its socket cannot be made user-only ({e.Message}).");
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
        "Aitm.Server listens on its local pipe/socket only and refuses a configured TCP endpoint: " +
        string.Join(", ", tcpConfigured) + ". Remove it and start again.");
    Environment.Exit(1);
    return;
}
// RESTRUCTURE.md "Slice 32b": a stop (POST /shutdown, Ctrl+C, a logoff) lets every call in flight finish.
// The longest call the server takes is a long /cli verb, so the drain waits that long, never the 30 s default.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = CliEndpoint.LongTimeout);

// The one open store per project (RESTRUCTURE.md "Slice 26") and the accessor every store-backed tool
// uses to resolve which project a request belongs to. Both are constructed here, not resolved from the
// container later, so McpToolFactory can close over the exact instances the framework will populate
// per request (Kestrel hosting sets the registered IHttpContextAccessor's HttpContext itself).
ProjectStore projectStore = new(dataDir);
Microsoft.AspNetCore.Http.HttpContextAccessor httpContextAccessor = new();
builder.Services.AddSingleton(projectStore);
builder.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(httpContextAccessor);

IReadOnlyList<McpServerTool> mcpTools = McpToolFactory.BuildTools(
    AllMcpTools.BuildRegistry(), projectStore, httpContextAccessor, projectRoot, dataDir);
builder.Services.AddMcpServer().WithHttpTransport().WithTools(mcpTools);

WebApplication app = builder.Build();

// Stopped, not Stopping: Stopping fires before Kestrel drains, so a call still in flight would lose its store
// (RESTRUCTURE.md slice 32b: the old server finishes its calls in flight, then exits).
app.Lifetime.ApplicationStopped.Register(projectStore.Dispose);

// On demand: the service exits when it has had no real call for the idle time. /health does not count.
TimeSpan idleTime = IdleExit.ConfiguredIdle();
IdleExit idleExit = new(idleTime, () => app.Lifetime.StopApplication());
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health") { await next(context); return; }
    using (idleExit.Begin()) await next(context);
});
using Timer idleTimer = new(_ => idleExit.CheckAndStopIfIdle(), null,
    TimeSpan.FromSeconds(1) < idleTime / 4 ? TimeSpan.FromSeconds(1) : idleTime / 4,
    TimeSpan.FromSeconds(1) < idleTime / 4 ? TimeSpan.FromSeconds(1) : idleTime / 4);

app.MapGet("/health", () => Results.Json(new
{
    pid = Environment.ProcessId,
    idleExitSeconds = (int)idleTime.TotalSeconds,
    version,
    buildStamp,
    uptimeSeconds = (DateTime.UtcNow - startedAt).TotalSeconds,
    openStores = projectStore.OpenInstances,
}));

// The 25 golden MCP tools. Reachable only over the pipe/socket above (no Host/Origin guard needed).
app.MapMcp("/mcp");

// RESTRUCTURE.md "Slice 34": Claude Code http hooks; runs the slice 20-22 handlers under the same
// per-project writer gate as /mcp (HookEndpoint).
app.MapPost("/hooks/{event}", (string @event, HttpContext context) => HookEndpoint.Handle(@event, context, projectStore));

// RESTRUCTURE.md "Slice 29c": the CLI verbs; each runs on the project's one open connection under its
// writer gate, with a timeout that answers exit 124 (CliEndpoint).
app.MapPost("/cli", (Func<HttpContext, Task<IResult>>)(context => CliEndpoint.Handle(context, projectStore, dataDir)));

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
    Console.Error.WriteLine("Aitm.Server bound a TCP address (" + string.Join(", ", tcpBound) + "); stopping.");
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

// Makes the top-level Program class visible to Aitm.Server.Tests' WebApplicationFactory<Program>.
public partial class Program;
