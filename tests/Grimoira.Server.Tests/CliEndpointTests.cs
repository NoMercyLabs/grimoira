using Grimoira.Store.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using Grimoira.Server.Data;
using Grimoira.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;
using System.Text.RegularExpressions;

namespace Grimoira.Server.Tests;

// RESTRUCTURE.md "Slice 29c: POST /cli on the server." Body { "args": [...], "cwd": "..." }, answer
// { "exitCode", "stdout", "stderr" }, behind the Host and Origin guard; the project comes from the request
// (Claude-Project-Dir header, else the body cwd), never from the server's own environment, and the verb
// runs under that project's writer gate. The oracle is CliDispatch.Run in-process and the bin-cli binary
// (slice 29b), each on its own fresh test instance. The server's own stores live in a temp data dir.
//
// This class both hosts its own in-memory server (WebApplicationFactory) and, via OldVsNewCli.BinCliDll(),
// spawns a real `dotnet bin-cli/grimoira.dll` process that forwards to the one shared grimoira server keyed
// by a single named pipe per data dir. CliDispatchTests does the same live-process call; running both
// classes at once raced them. RealBinCliCollection serializes every class that makes that live call.
[Collection(RealBinCliCollection.Name)]
public sealed partial class CliEndpointTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimoira-cli-").FullName;
    private readonly List<string> _projectDirs = [];
    private readonly List<string> _cliInstances = [];
    private const string Port = "17643";
    private readonly string _allowedHost = $"127.0.0.1:{Port}";

    private sealed record CliAnswer(HttpStatusCode Status, int ExitCode, string Stdout, string Stderr);

    private readonly string? _savedDataDir = Environment.GetEnvironmentVariable("GRIMOIRA_DATA_DIR");
    private readonly string? _savedServerPort = Environment.GetEnvironmentVariable("GRIMOIRA_SERVER_PORT");

    private WebApplicationFactory<Program> Factory()
    {
        Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", _dataDir);
        Environment.SetEnvironmentVariable("GRIMOIRA_SERVER_PORT", Port);
        return new WebApplicationFactory<Program>();
    }

    // The in-process host above (Factory()) runs Program's own top-level startup and holds _dataDir's
    // server.lock for the test's lifetime. A real `dotnet bin-cli/grimoira.dll` process started against
    // that same data dir cannot reach a service over the pipe (Factory()'s TestServer never binds one)
    // and, on trying to start its own, cannot take a lock the in-process host already holds, so it never
    // answers /health. The bin-cli comparison needs its own real, unshared data dir.
    private readonly string _binDataDir = Directory.CreateTempSubdirectory("grimoira-cli-bin-").FullName;

    private OldVsNewCli.Result RunBin(string instance, string arguments)
    {
        string? saved = Environment.GetEnvironmentVariable("GRIMOIRA_DATA_DIR");
        Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", _binDataDir);
        try
        {
            return OldVsNewCli.Run(OldVsNewCli.BinCliDll(), instance, arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", saved);
        }
    }

    private HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{_allowedHost}") });

    private string NewProjectDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"test-cli-srv-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _projectDirs.Add(dir);
        return dir;
    }

    private string NewCliInstance(string label)
    {
        string instance = GrimoiraCliRunner.NewTestInstance(label);
        _cliInstances.Add(instance);
        return instance;
    }

    private async Task<CliAnswer> PostCli(HttpClient client, string[] args,
        string? projectDir = null, string? instanceHeader = null, string? cwd = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/cli");
        request.Headers.Host = _allowedHost;
        if (projectDir is not null) request.Headers.TryAddWithoutValidation(RequestProjectResolver.ProjectDirHeader, projectDir);
        if (instanceHeader is not null) request.Headers.TryAddWithoutValidation(RequestProjectResolver.InstanceHeader, instanceHeader);
        request.Content = new StringContent(JsonSerializer.Serialize(new { args, cwd = cwd ?? projectDir ?? Path.GetTempPath() }),
            Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK) return new CliAnswer(response.StatusCode, -1, body, "");
        using JsonDocument doc = JsonDocument.Parse(body);
        return new CliAnswer(response.StatusCode,
            doc.RootElement.GetProperty("exitCode").GetInt32(),
            doc.RootElement.GetProperty("stdout").GetString()!,
            doc.RootElement.GetProperty("stderr").GetString()!);
    }

    private static (int exit, string stdout, string stderr) InProcess(string[] args, string instance)
    {
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = CliDispatch.Run([.. args, "--instance", instance], Directory.GetCurrentDirectory(), stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    public static TheoryData<string, string[]> Cases() => new()
    {
        { "help", ["help"] },
        { "todos", ["todos"] },
        { "unknown-verb", ["no-such-verb"] },
        { "dropped-verb", ["loop"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CliRouteEqualsInProcessAndTheBinCliBinary(string name, string[] args)
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);

        (int exit, string stdout, string stderr) inProc = InProcess(args, NewCliInstance("cliroute-" + name));
        OldVsNewCli.Result bin = RunBin(NewCliInstance("cliroute-bin-" + name), string.Join(' ', args));
        CliAnswer route = await PostCli(client, args, NewProjectDir(name));
        Assert.Equal(HttpStatusCode.OK, route.Status);
        Assert.Equal(bin.ExitCode, inProc.exit);
        Assert.Equal(bin.ExitCode, route.ExitCode);
        Assert.Equal(bin.Stdout, inProc.stdout);
        Assert.Equal(bin.Stdout, route.Stdout);
        Assert.Equal(bin.Stderr, route.Stderr);
    }

    [Fact]
    public async Task AddThenQueryEqualsInProcessAndTheBinCliBinary()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        string[] add = ["add", "--term", "cliroutefixture", "--value", "a value seeded through slash cli"];
        string[] query = ["query", "cliroutefixture"];
        string inProcInstance = NewCliInstance("cliroute-addq");
        string binInstance = NewCliInstance("cliroute-bin-addq");
        string projectDir = NewProjectDir("addq");

        (int exit, string stdout, string stderr) inProcAdd = InProcess(add, inProcInstance);
        (int exit, string stdout, string stderr) inProcQuery = InProcess(query, inProcInstance);
        OldVsNewCli.Result binAdd = RunBin(binInstance,
            "add --term cliroutefixture --value \"a value seeded through slash cli\"");
        OldVsNewCli.Result binQuery = RunBin(binInstance, "query cliroutefixture");
        CliAnswer routeAdd = await PostCli(client, add, projectDir);
        CliAnswer routeQuery = await PostCli(client, query, projectDir);

        Assert.Equal(binAdd.ExitCode, routeAdd.ExitCode);
        Assert.Equal(binAdd.Stdout, routeAdd.Stdout);
        Assert.Equal(binAdd.Stdout, inProcAdd.stdout);
        Assert.Equal(binQuery.ExitCode, routeQuery.ExitCode);
        Assert.Contains("a value seeded through slash cli", binQuery.Stdout);
        // query ends with its own elapsed time, "(0,32ms)", which differs run to run.
        static string NoTiming(string text) => ParenthesisedTimingMs().Replace(text, "(Tms)");
        Assert.Equal(NoTiming(inProcQuery.stdout), NoTiming(routeQuery.Stdout));
        // The spawned binary writes its U+2022 bullet through the console code page (OEM 437 on Windows,
        // byte 0x07), which OldVsNewCli reads back as BEL; the route and the in-process run answer in UTF-8.
        Assert.Equal(NoTiming(binQuery.Stdout.Replace('\a', '\u2022')), NoTiming(routeQuery.Stdout));
    }

    [Fact]
    public async Task AnExceptionInsideAVerbIsExitOneAndTheServerStillAnswers()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        string missing = Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}", "none.db");
        string projectDir = NewProjectDir("throws");

        CliAnswer answer = await PostCli(client, ["import", "--from", missing], projectDir);

        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal(1, answer.ExitCode);
        Assert.StartsWith("error:", answer.Stderr);
        using HttpResponseMessage health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        CliAnswer after = await PostCli(client, ["todos"], projectDir);
        Assert.Equal(0, after.ExitCode);
    }

    [Fact]
    public async Task TwoProjectsStayApartAndTheServerEnvDoesNotPinThem()
    {
        string? savedProjectDir = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR");
        string? savedInstance = Environment.GetEnvironmentVariable("GRIMOIRA_INSTANCE");
        Environment.SetEnvironmentVariable("CLAUDE_PROJECT_DIR", NewProjectDir("server-env"));
        Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", null);
        try
        {
            using WebApplicationFactory<Program> factory = Factory();
            using HttpClient client = Client(factory);
            string projectA = NewProjectDir("a");
            string projectB = NewProjectDir("b");

            CliAnswer addA = await PostCli(client, ["add", "--term", "onlyinprojecta", "--value", "project a value"], projectA);
            CliAnswer queryA = await PostCli(client, ["query", "onlyinprojecta"], projectA);
            CliAnswer queryB = await PostCli(client, ["query", "onlyinprojecta"], projectB);
            // No header: the body cwd names the project, and it is still not the server's env project.
            CliAnswer queryByCwd = await PostCli(client, ["query", "onlyinprojecta"], cwd: projectA);

            Assert.Equal(0, addA.ExitCode);
            Assert.Contains("project a value", queryA.Stdout);
            Assert.DoesNotContain("project a value", queryB.Stdout);
            Assert.Contains("project a value", queryByCwd.Stdout);
            string[] stores = [.. Directory.GetDirectories(_dataDir).Select(d => Path.GetFileName(d.AsSpan()).ToString())];
            Assert.Contains(Path.GetFileName(projectA), stores);
            Assert.Contains(Path.GetFileName(projectB), stores);
            Assert.DoesNotContain(stores, s => s.Contains("server-env"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_PROJECT_DIR", savedProjectDir);
            Environment.SetEnvironmentVariable("GRIMOIRA_INSTANCE", savedInstance);
        }
    }

    [Fact]
    public async Task TenCliWritesAndTenMcpWritesAtOnceGiveNoLockError()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        string instance = "test-cli-concurrency-" + Guid.NewGuid().ToString("N");

        HttpClientTransportOptions options = new()
        {
            Endpoint = new Uri(client.BaseAddress!, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Host"] = _allowedHost,
                [RequestProjectResolver.InstanceHeader] = instance,
            },
        };
        await using McpClient mcp = await McpClient.CreateAsync(new HttpClientTransport(options, client));

        Task<CliAnswer>[] cliWrites = [.. Enumerable.Range(0, 10).Select(i => PostCli(client, ["add", "--term", $"concurrentterm{i}", "--value", $"value {i}"], instanceHeader: instance))];
        Task<ModelContextProtocol.Protocol.CallToolResult>[] mcpWrites = [.. Enumerable.Range(0, 10)
            .Select(i => mcp.CallToolAsync("brain_learn", new Dictionary<string, object?>
            {
                ["kind"] = "node",
                ["key"] = $"concurrent-node-{i}",
                ["a"] = "fact",
                ["b"] = "cli and mcp concurrency fixture",
                ["c"] = "seeded by CliEndpointTests",
            }).AsTask())];
        await Task.WhenAll(cliWrites);
        await Task.WhenAll(mcpWrites);

        foreach (CliAnswer answer in cliWrites.Select(t => t.Result))
        {
            Assert.Equal(0, answer.ExitCode);
            Assert.DoesNotContain("locked", answer.Stderr + answer.Stdout, StringComparison.OrdinalIgnoreCase);
        }
        foreach (ModelContextProtocol.Protocol.CallToolResult result in mcpWrites.Select(t => t.Result))
        {
            Assert.NotEqual(true, result.IsError);
            string text = string.Join("\n", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text));
            Assert.DoesNotContain("locked", text, StringComparison.OrdinalIgnoreCase);
        }
        CliAnswer todos = await PostCli(client, ["query", "concurrentterm7"], instanceHeader: instance);
        Assert.Contains("value 7", todos.Stdout);
    }

    [Fact]
    public async Task ATimedOutCallIsExit124AndTheServerStaysAlive()
    {
        string? saved = Environment.GetEnvironmentVariable("GRIMOIRA_CLI_TIMEOUT_SECONDS");
        Environment.SetEnvironmentVariable("GRIMOIRA_CLI_TIMEOUT_SECONDS", "1");
        try
        {
            using WebApplicationFactory<Program> factory = Factory();
            using HttpClient client = Client(factory);
            string instance = "test-cli-timeout-" + Guid.NewGuid().ToString("N");
            ProjectHandle handle = factory.Services.GetRequiredService<ProjectStore>().Acquire(instance);

            // Something else (a long /mcp or /cli call) holds the project's gate past the timeout.
            await handle.Gate.WaitAsync();
            CliAnswer timedOut;
            try
            {
                timedOut = await PostCli(client, ["todos"], instanceHeader: instance);
            }
            finally
            {
                handle.Gate.Release();
            }

            Assert.Equal(HttpStatusCode.OK, timedOut.Status);
            Assert.Equal(124, timedOut.ExitCode);
            Assert.StartsWith("error: timed out", timedOut.Stderr);
            using HttpResponseMessage health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            CliAnswer after = await PostCli(client, ["todos"], instanceHeader: instance);
            Assert.Equal(0, after.ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRIMOIRA_CLI_TIMEOUT_SECONDS", saved);
        }
    }

    public void Dispose()
    {
        // Factory() sets these process-wide (Environment.SetEnvironmentVariable has no per-instance scope),
        // so every later test in this process — including a real bin-cli child process another test spawns
        // (OldVsNewCli.BinCliDll's no-golden fallback) — would otherwise inherit this test's now-deleted
        // _dataDir as GRIMOIRA_DATA_DIR and find no server there.
        Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", _savedDataDir);
        Environment.SetEnvironmentVariable("GRIMOIRA_SERVER_PORT", _savedServerPort);
        foreach (string instance in _cliInstances) GrimoiraCliRunner.DeleteInstance(instance);
        foreach (string dir in _projectDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GrimoiraCliRunner.RunBinCli("service stop", _binDataDir);
        for (int attempt = 0; Directory.Exists(_binDataDir); attempt++)
        {
            try { Directory.Delete(_binDataDir, recursive: true); break; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(250); }
        }
        Assert.False(Directory.Exists(_binDataDir), "bin CLI test store survived cleanup");
    }

    [GeneratedRegex(@"\(\d+[.,]\d+ms\)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ParenthesisedTimingMs();
}
