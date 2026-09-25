using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Aitm.Server.Data;

// RESTRUCTURE.md "Slice 25: Aitm.Server host." A Kestrel host on 127.0.0.1:7635 only. It rejects a
// wrong Host header and any request carrying an Origin header (a browser reaching localhost). /health
// needs no auth; every other route needs "Authorization: Bearer <server.token>". Single instance per
// data directory via ProcessOwner.TryAcquireSingleInstanceLock. No /mcp and no hook routes yet — those
// land in slice 26 onward. The data directory and port are read from environment variables (not hard
// coded to ~/.aitm or 7635) so WebApplicationFactory-based tests never touch a real store or bind the
// real port; production has no reason to set either and gets the real defaults.

string dataDir = Environment.GetEnvironmentVariable("AITM_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm");
int port = int.TryParse(Environment.GetEnvironmentVariable("AITM_SERVER_PORT"), out int configuredPort) ? configuredPort : 7635;

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

WebApplication app = builder.Build();

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
    openStores = Array.Empty<string>(),
}));

app.Run();

// Makes the top-level Program class visible to Aitm.Server.Tests' WebApplicationFactory<Program>.
public partial class Program;
