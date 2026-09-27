using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Grimora.Layout.Tests;
using Grimora.Server.Data;
using Grimora.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using Xunit;

namespace Grimora.Server.Tests;

// RESTRUCTURE.md "Slice 34: POST /hooks/{event} on the server." The body is the Claude Code hook JSON
// (the payload `grimora hook <event>` reads on stdin); the answer is the handler's stdout, 200. The oracle
// is what `grimora hook <event>` runs today: src/Grimora.Cli's Program.RunHook (grimora.dll), spawned on the
// same payload. The hook handlers keep their own ~/.grimora/<instance> layout (HookPaths), so each test
// uses a unique test-* instance there and deletes it afterwards, the same way Grimora.Hooks.Tests does.
public sealed class HooksEndpointTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimora-hooks-").FullName;
    private readonly string _projectDir;
    private readonly string _instance;
    private const string Port = "17641";
    private readonly string _allowedHost = $"127.0.0.1:{Port}";

    public HooksEndpointTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"test-hooks-srv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
        _instance = Path.GetFileName(_projectDir).ToLowerInvariant();
    }

    private WebApplicationFactory<Program> Factory()
    {
        Environment.SetEnvironmentVariable("GRIMORA_DATA_DIR", _dataDir);
        Environment.SetEnvironmentVariable("GRIMORA_SERVER_PORT", Port);
        return new WebApplicationFactory<Program>();
    }

    private HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{_allowedHost}") });

    private async Task<(HttpStatusCode status, string body)> PostHook(HttpClient client, string eventName, string payload, bool sendProjectHeader = true)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"/hooks/{eventName}");
        request.Headers.Host = _allowedHost;
        if (sendProjectHeader) request.Headers.TryAddWithoutValidation(RequestProjectResolver.ProjectDirHeader, _projectDir);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // `grimora hook <event>` as the CLI runs it today: src/Grimora.Cli built beside this test assembly (same
    // configuration), payload on stdin, CLAUDE_PROJECT_DIR set the way Claude Code sets it for a command hook.
    private string RunCliHook(string eventName, string payload)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
        string dll = Path.Combine(RepoPaths.Root, "src", "Grimora.Cli", "bin", configuration, "net10.0", "grimora.dll");
        Assert.True(File.Exists(dll), $"Grimora.Cli not built at {dll}");
        ProcessStartInfo psi = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("hook");
        psi.ArgumentList.Add(eventName);
        psi.Environment["CLAUDE_PROJECT_DIR"] = _projectDir;
        // The CLI sends SessionEnd and PostToolUse on to a server; this factory's server listens on no pipe,
        // so those events reach nothing and fail open, and never the live server on the default data dir's pipe.
        psi.Environment["GRIMORA_DATA_DIR"] = _dataDir;
        using Process process = Process.Start(psi)!;
        process.StandardInput.Write(payload);
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return stdout;
    }

    private string WriteTranscript()
    {
        string transcriptPath = Path.Combine(_projectDir, "t.jsonl");
        File.WriteAllLines(transcriptPath,
        [
            JsonSerializer.Serialize(new { type = "user", message = new { content = "Ship the hooks route, end to end, today." } }),
        ]);
        return transcriptPath;
    }

    // HookPaths (Grimora.Hooks) honours GRIMORA_DATA_DIR the same way the CLI/server do (a fix to a
    // reviewer-reported bug: hooks used to hardcode ~/.grimora regardless of GRIMORA_DATA_DIR), so this
    // test's own db path must be under this test's _dataDir too, not the real ~/.grimora — otherwise every
    // assertion here would look in the wrong place for what the hook handlers, correctly, now write under
    // _dataDir (set via the GRIMORA_DATA_DIR env var above).
    private string HookDbPath => Path.Combine(_dataDir, _instance, "grimora.db");

    [Fact]
    public async Task PreCompactEqualsTheCliHook()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        string payload = JsonSerializer.Serialize(new { transcript_path = WriteTranscript(), cwd = _projectDir, session_id = "s1" });

        string expected = RunCliHook("PreCompact", payload);
        (HttpStatusCode status, string body) = await PostHook(client, "PreCompact", payload);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Ship the hooks route", expected);
        Assert.Equal(expected, body);
    }

    [Fact]
    public async Task UserPromptSubmitEqualsTheCliHook()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        string briefPath = Path.Combine(Path.GetDirectoryName(HookDbPath)!, "compact", "s2.md");
        void SeedBrief()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(briefPath)!);
            File.WriteAllText(briefPath, "# Carried across compaction\n\n- \"a directive long enough to be kept\"\n");
        }
        string payload = JsonSerializer.Serialize(new { cwd = _projectDir, session_id = "s2", prompt = "go on" });

        SeedBrief();
        string expected = RunCliHook("UserPromptSubmit", payload);
        SeedBrief();
        (HttpStatusCode status, string body) = await PostHook(client, "UserPromptSubmit", payload);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("hookSpecificOutput", expected);
        Assert.Equal(expected, body);
    }

    [Fact]
    public async Task PostToolUseBashEqualsTheCliHookAndRecordsThePattern()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        CreateEmptyHookDb();
        string payload = JsonSerializer.Serialize(new { cwd = _projectDir, session_id = "s3", tool_name = "Bash", tool_input = new { command = "dotnet test Grimora.sln" } });

        string expected = RunCliHook("PostToolUse", payload);
        (HttpStatusCode status, string body) = await PostHook(client, "PostToolUse", payload);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, body);
        // The CLI does not run PatternWatch today (it prints nothing for PostToolUse), so the server
        // running it is proved by the row it records.
        Assert.Equal(1L, Scalar(HookDbPath, "SELECT count(*) FROM patterns"));
    }

    [Fact]
    public async Task PostToolUseEditEqualsTheCliHook()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        string payload = JsonSerializer.Serialize(new { cwd = _projectDir, session_id = "s4", tool_name = "Edit", tool_input = new { file_path = Path.Combine(_projectDir, "x.txt") } });

        string expected = RunCliHook("PostToolUse", payload);
        (HttpStatusCode status, string body) = await PostHook(client, "PostToolUse", payload);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, body);
    }

    [Fact]
    public async Task SessionEndEqualsTheCliHookAndIndexesTheTranscript()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        GrimoraCliRunner.Run($"init --instance {_instance}");
        string payload = JsonSerializer.Serialize(new { cwd = _projectDir, session_id = "s5", transcript_path = WriteTranscript(), reason = "exit" });

        string expected = RunCliHook("SessionEnd", payload);
        (HttpStatusCode status, string body) = await PostHook(client, "SessionEnd", payload);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, body);
        Assert.True(Scalar(HookDbPath, "SELECT count(*) FROM chat") > 0, "SessionEnd did not index the transcript");
    }

    // The server is one process for every session. Started from a session hook, it inherits that session's
    // CLAUDE_PROJECT_DIR; a handler that preferred the process env would read and write the files of that
    // one project for every request. The project a request names (its Claude-Project-Dir header) wins.
    private async Task WithServerEnvPointingElsewhere(Func<Task> body)
    {
        string elsewhere = Path.Combine(Path.GetTempPath(), $"test-hooks-elsewhere-{Guid.NewGuid():N}");
        string? before = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_PROJECT_DIR", elsewhere);
        try
        {
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_PROJECT_DIR", before);
            GrimoraCliRunner.DeleteInstance(Path.GetFileName(elsewhere).ToLowerInvariant());
        }
    }

    [Fact]
    public async Task UserPromptSubmitRestoresTheBriefOfTheRequestProjectNotTheServerEnvProject()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        string briefPath = Path.Combine(Path.GetDirectoryName(HookDbPath)!, "compact", "s7.md");
        Directory.CreateDirectory(Path.GetDirectoryName(briefPath)!);
        File.WriteAllText(briefPath, "# Carried across compaction\n\n- \"a directive long enough to be kept\"\n");
        string payload = JsonSerializer.Serialize(new { cwd = _projectDir, session_id = "s7", prompt = "go on" });

        await WithServerEnvPointingElsewhere(async () =>
        {
            (HttpStatusCode status, string body) = await PostHook(client, "UserPromptSubmit", payload);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("a directive long enough to be kept", body);
        });
    }

    [Fact]
    public async Task PreCompactWritesTheBriefUnderTheRequestProjectNotTheServerEnvProject()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        string payload = JsonSerializer.Serialize(new { transcript_path = WriteTranscript(), cwd = _projectDir, session_id = "s8" });

        await WithServerEnvPointingElsewhere(async () =>
        {
            (HttpStatusCode status, _) = await PostHook(client, "PreCompact", payload);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(HookDbPath)!, "compact", "s8.md")),
                "the PreCompact brief was not written under the request's project");
        });
    }

    [Fact]
    public async Task UnknownEventIs200AndEmpty()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);

        (HttpStatusCode status, string body) = await PostHook(client, "NoSuchEvent", $$"""{"cwd":{{JsonSerializer.Serialize(_projectDir)}}}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", body);
    }

    [Theory]
    [InlineData("PreCompact")]
    [InlineData("SessionEnd")]
    [InlineData("PostToolUse")]
    public async Task MalformedPayloadIs200AndEmpty(string eventName)
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);

        (HttpStatusCode status, string body) = await PostHook(client, eventName, "{not json", sendProjectHeader: false);
        (HttpStatusCode status2, string body2) = await PostHook(client, eventName, "[1,2]", sendProjectHeader: false);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", body);
        Assert.Equal(HttpStatusCode.OK, status2);
        Assert.Equal("", body2);
    }

    [Fact]
    public async Task ConcurrentHooksAndMcpCallsOnOneProjectNeverHitALockError()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = Client(factory);
        CreateEmptyHookDb();

        HttpClientTransportOptions options = new()
        {
            Endpoint = new Uri(client.BaseAddress!, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Host"] = _allowedHost,
                [RequestProjectResolver.ProjectDirHeader] = _projectDir,
            },
        };
        await using McpClient mcp = await McpClient.CreateAsync(new HttpClientTransport(options, client));

        List<Task<(HttpStatusCode, string)>> hookCalls = [.. Enumerable.Range(0, 10).Select(i => PostHook(client, "PostToolUse",
            JsonSerializer.Serialize(new { cwd = _projectDir, session_id = "s6", tool_name = "Bash", tool_input = new { command = $"git commit -m c{i}" } })))];
        List<Task<ModelContextProtocol.Protocol.CallToolResult>> mcpCalls = [.. Enumerable.Range(0, 10).Select(i => mcp.CallToolAsync("brain_learn", new Dictionary<string, object?>
        {
            ["kind"] = "node",
            ["key"] = $"hooks-concurrency-{i}",
            ["a"] = "fact",
            ["b"] = "hooks concurrency fixture",
            ["c"] = "seeded by HooksEndpointTests",
        }).AsTask())];

        (HttpStatusCode, string)[] hookResults = await Task.WhenAll(hookCalls);
        ModelContextProtocol.Protocol.CallToolResult[] mcpResults = await Task.WhenAll(mcpCalls);

        Assert.All(hookResults, r => Assert.Equal(HttpStatusCode.OK, r.Item1));
        foreach (ModelContextProtocol.Protocol.CallToolResult r in mcpResults)
        {
            Assert.NotEqual(true, r.IsError);
            string text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(r.Content)).Text;
            Assert.DoesNotContain("database is locked", text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(10L, Scalar(HookDbPath, "SELECT coalesce(sum(count),0) FROM patterns WHERE kind = 'command'"));
    }

    private void CreateEmptyHookDb()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HookDbPath)!);
        using SqliteConnection connection = new($"Data Source={HookDbPath}");
        connection.Open();
    }

    private static long Scalar(string dbPath, string sql)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GRIMORA_DATA_DIR", null);
        Environment.SetEnvironmentVariable("GRIMORA_SERVER_PORT", null);
        SqliteConnection.ClearAllPools();
        GrimoraCliRunner.DeleteInstance(_instance);
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort cleanup */ }
        try { Directory.Delete(_projectDir, recursive: true); } catch { /* best effort cleanup */ }
    }
}
