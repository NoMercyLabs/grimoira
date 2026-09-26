using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Aitm.Server.Data;
using ModelContextProtocol.Server;

// RESTRUCTURE.md "Slice 25: Aitm.Server host." A Kestrel host on 127.0.0.1:7635 only. It rejects a
// wrong Host header and any request carrying an Origin header (a browser reaching localhost). /health
// needs no auth; every other route needs "Authorization: Bearer <server.token>". Single instance per
// data directory via ProcessOwner.TryAcquireSingleInstanceLock.
//
// RESTRUCTURE.md "Slice 26: /mcp on the server, beside mcp.cs." Adds the 25 golden MCP tools at /mcp,
// behind the same bearer-auth middleware as every other non-/health route. mcp.cs stays the working
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

string token = ServerToken.EnsureToken(dataDir);
DateTime startedAt = DateTime.UtcNow;
string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "dev";

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

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

app.Lifetime.ApplicationStopping.Register(projectStore.Dispose);

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

    bool isHealth = string.Equals(context.Request.Path.Value, "/health", StringComparison.OrdinalIgnoreCase);
    if (!isHealth)
    {
        AuthenticationHeaderValue? auth = context.Request.Headers.Authorization.Count > 0
            && AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out AuthenticationHeaderValue? parsed)
            ? parsed
            : null;
        string? presented = auth is { Scheme: "Bearer", Parameter: not null } ? auth.Parameter : null;
        bool authorized = presented is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token));
        if (!authorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("unauthorized");
            return;
        }
    }

    await next();
});

app.MapGet("/health", () => Results.Json(new
{
    version,
    uptimeSeconds = (DateTime.UtcNow - startedAt).TotalSeconds,
    openStores = projectStore.OpenInstances,
}));

// The 25 golden MCP tools, behind the same auth middleware as every non-/health route above.
app.MapMcp("/mcp");

// RESTRUCTURE.md "Slice 34": Claude Code http hooks, behind the same auth middleware; runs the
// slice 20-22 handlers under the same per-project writer gate as /mcp (HookEndpoint).
app.MapPost("/hooks/{event}", (string @event, HttpContext context) => HookEndpoint.Handle(@event, context, projectStore));

// RESTRUCTURE.md "Slice 29c": the CLI verbs, behind the same auth middleware; each runs on the project's
// one open connection under its writer gate, with a timeout that answers exit 124 (CliEndpoint).
app.MapPost("/cli", (Func<HttpContext, Task<IResult>>)(context => CliEndpoint.Handle(context, projectStore, dataDir)));

app.Run();

// Makes the top-level Program class visible to Aitm.Server.Tests' WebApplicationFactory<Program>.
public partial class Program;
