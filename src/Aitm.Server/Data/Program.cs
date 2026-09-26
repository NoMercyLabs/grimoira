using System.Reflection;
using Aitm.Server.Data;
using ModelContextProtocol.Server;

// RESTRUCTURE.md "Slice 25: Aitm.Server host." A Kestrel host on 127.0.0.1:7635 only. It rejects a
// wrong Host header and any request carrying an Origin header (a browser reaching localhost). It holds no
// secret and checks no token (the owner, 2026-09-26: "AITM is for remembering everything"; authenticated
// traffic is Arcanum's job): the guard is the loopback bind, the Host check and the Origin refusal. An old
// server.token file is ignored, never deleted. Single instance per data directory via
// ProcessOwner.TryAcquireSingleInstanceLock.
//
// RESTRUCTURE.md "Slice 26: /mcp on the server, beside mcp.cs." Adds the 25 golden MCP tools at /mcp,
// behind the same guard as every other route. mcp.cs stays the working
// host for the live session (.mcp.json still starts it) until phase 3 switches over.

string dataDir = Environment.GetEnvironmentVariable("AITM_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm");
int port = int.TryParse(Environment.GetEnvironmentVariable("AITM_SERVER_PORT"), out int configuredPort) ? configuredPort : 7635;
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

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
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

string[] allowedHosts = [$"127.0.0.1:{port}", $"localhost:{port}"];

app.Use(async (context, next) =>
{
    string? host = context.Request.Host.Value;
    if (host is null || Array.IndexOf(allowedHosts, host) < 0)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("host not allowed");
        return;
    }

    if (context.Request.Headers.ContainsKey("Origin"))
    {
        // Section 5, "Network path": a web page in a local browser can reach 127.0.0.1; an Origin
        // header is only ever sent by a browser (or something imitating one), so it is refused outright.
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("browser origin not allowed");
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Json(new
{
    version,
    buildStamp,
    uptimeSeconds = (DateTime.UtcNow - startedAt).TotalSeconds,
    openStores = projectStore.OpenInstances,
}));

// The 25 golden MCP tools, behind the same Host and Origin guard as every route above.
app.MapMcp("/mcp");

// RESTRUCTURE.md "Slice 34": Claude Code http hooks, behind the same guard; runs the
// slice 20-22 handlers under the same per-project writer gate as /mcp (HookEndpoint).
app.MapPost("/hooks/{event}", (string @event, HttpContext context) => HookEndpoint.Handle(@event, context, projectStore));

// RESTRUCTURE.md "Slice 29c": the CLI verbs, behind the same guard; each runs on the project's
// one open connection under its writer gate, with a timeout that answers exit 124 (CliEndpoint).
app.MapPost("/cli", (Func<HttpContext, Task<IResult>>)(context => CliEndpoint.Handle(context, projectStore, dataDir)));

// RESTRUCTURE.md "Slice 32b": the SessionStart of a newer build asks this server to make way. It stops
// taking new connections, finishes the calls in flight, and exits; that frees server.lock for the new build.
app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.Accepted();
});

app.Run();

// Makes the top-level Program class visible to Aitm.Server.Tests' WebApplicationFactory<Program>.
public partial class Program;
