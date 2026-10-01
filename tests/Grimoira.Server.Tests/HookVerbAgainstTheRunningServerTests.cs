using System.Diagnostics;
using System.Text.Json;
using Grimoira.Layout.Tests;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Grimoira.Server.Tests;

// RESTRUCTURE.md slice 30, transport updated by Slice P1: hooks.json runs `grimoira hook <event>` from the
// published CLI. The two compact handlers run inside the CLI; the SessionEnd and PostToolUse handlers need
// Memory, Docs, Graph and Store, so the CLI sends those events to the server's POST /hooks/{event} over the
// local pipe / Unix socket and exits 0 on any failure. A real Grimoira.Server process (this build's output)
// runs against a temp data dir; the CLI (Grimoira.Cli's build output, the same project build-cli.ps1 publishes
// to bin-cli) runs as its own process with that same data dir. The handlers (HookPaths) honour that same
// GRIMOIRA_DATA_DIR, so each test uses a unique instance under _dataDir and deletes it afterwards, the same
// way Grimoira.Hooks.Tests does.
public sealed class HookVerbAgainstTheRunningServerTests : IDisposable
{
    private static readonly string Configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')).Parent!.Name;
    private static readonly string ServerDll = Path.Combine(RepoPaths.Root, "src", "Grimoira.Server", "bin", Configuration, "net10.0", "Grimoira.Server.dll");
    private static readonly string CliDll = Path.Combine(RepoPaths.Root, "src", "Grimoira.Cli", "bin", Configuration, "net10.0", "grimoira.dll");

    private readonly ITestOutputHelper _output;
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimoira-hook-verb-").FullName;
    private readonly string _projectDir;
    private readonly string _instance;
    private readonly List<Process> _servers = [];

    private readonly string? _previousDataDirEnv;

    public HookVerbAgainstTheRunningServerTests(ITestOutputHelper output)
    {
        _output = output;
        _projectDir = Path.Combine(Path.GetTempPath(), $"test-hook-verb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
        _instance = Path.GetFileName(_projectDir).ToLowerInvariant();

        // GrimoiraCliRunner.Run below (`init`/`project`) runs Grimoira.Server's CliDispatch in THIS process,
        // which reads GRIMOIRA_DATA_DIR from this process's own env (there is no dataDir parameter to pass
        // explicitly) — it must agree with the spawned server/CLI processes' GRIMOIRA_DATA_DIR=_dataDir
        // (StartServer/RunHook below), or the golden `init`/`project` calls register a project the hook
        // handlers, correctly honouring GRIMOIRA_DATA_DIR, can never find.
        _previousDataDirEnv = Environment.GetEnvironmentVariable("GRIMOIRA_DATA_DIR");
        Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", _dataDir);
    }

    // HookPaths (Grimoira.Hooks) honours GRIMOIRA_DATA_DIR the same way the CLI/server do (a fix to a
    // reviewer-reported bug: hooks used to hardcode ~/.grimoira regardless of GRIMOIRA_DATA_DIR) — this class
    // runs its server/CLI with GRIMOIRA_DATA_DIR=_dataDir (see StartServer/RunHook below), so the db the
    // hook handlers actually write to lives under _dataDir, not GrimoiraCliRunner's default ~/.grimoira.
    private string DbPath => Path.Combine(_dataDir, _instance, "grimoira.db");

    [Fact]
    public void PreCompactThenUserPromptSubmitThroughTheCliCarryTheBriefOnceWithNoServer()
    {
        string payload = JsonSerializer.Serialize(new { transcript_path = WriteTranscript(), cwd = _projectDir, session_id = "s1" });
        string prompt = JsonSerializer.Serialize(new { cwd = _projectDir, session_id = "s1", prompt = "go on" });

        (int briefExit, string brief, _) = RunHook("PreCompact", payload);
        (int firstExit, string first, _) = RunHook("UserPromptSubmit", prompt);
        (int secondExit, string second, _) = RunHook("UserPromptSubmit", prompt);

        Assert.Equal(0, briefExit);
        Assert.Contains("Ship the hook slots on the published CLI.", brief);
        Assert.Equal(0, firstExit);
        using JsonDocument doc = JsonDocument.Parse(first);
        Assert.Equal("UserPromptSubmit", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
        Assert.Contains("Ship the hook slots on the published CLI.", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
        Assert.Equal(0, secondExit);
        Assert.Equal("", second);
    }

    [Fact]
    public async Task SessionEndThroughTheCliIndexesTheTranscriptTheClaudeDirAndTheCode()
    {
        StartServer();
        string fixtureRoot = Path.Combine(_projectDir, "fixture");
        Directory.CreateDirectory(fixtureRoot);
        File.WriteAllText(Path.Combine(fixtureRoot, "widget.ts"), "export class Widget {}\n");
        string claudeDir = Path.Combine(_projectDir, ".claude");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(Path.Combine(claudeDir, "spec.md"), "# A real spec\n\nEnough body text to survive the compaction filter.\n");
        GrimoiraCliRunner.Run($"init --instance {_instance}");
        GrimoiraCliRunner.Run($"project --instance {_instance} --name web --root \"{fixtureRoot}\" --globs \"*.ts\"");
        string payload = JsonSerializer.Serialize(new { cwd = _projectDir, session_id = "s2", transcript_path = WriteTranscript(), reason = "exit" });

        (int exit, string stdout, _) = RunHook("SessionEnd", payload);

        Assert.Equal(0, exit);
        Assert.Equal("", stdout);
        // SessionEnd answers as soon as the CLI has forwarded the payload to the server (slice 38:
        // IndexJobQueue), before the actual indexing runs - the 1.5 s hooks.json budget is the server's
        // own HTTP answer, not the indexing itself. So this waits for the real, bounded completion signal
        // (the rows landing) instead of assuming it already happened the instant the process exited; a
        // failure that a retry inside the server could not clear would instead show up as a finding
        // (IndexJobQueueTests covers that path directly, against the queue, not through two spawned processes).
        Assert.True(await PollUntil(() => Scalar("SELECT count(*) FROM chat") > 0, TimeSpan.FromSeconds(25)),
            "SessionEnd did not index the transcript");
        Assert.True(await PollUntil(() => Scalar("SELECT count(*) FROM docs") > 0, TimeSpan.FromSeconds(25)),
            "SessionEnd did not index the .claude dir");
        Assert.True(await PollUntil(() => Scalar("SELECT count(*) FROM edges WHERE symbol='Widget'") == 1, TimeSpan.FromSeconds(25)),
            "SessionEnd did not index the code");
    }

    // The background job (IndexJobQueue) may still be mid-Acquire (creating/migrating the db) or not yet
    // dequeued when the first poll runs, so a query against a table it has not created yet throws instead
    // of just returning 0 - that is a "not ready", not a real failure, so it is swallowed here the same way
    // "not yet true" is; only a timeout without ever seeing a clean true is a real failure.
    private static async Task<bool> PollUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return true; }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* db/table not there yet - keep polling */ }
            await Task.Delay(50);
        }
        try { return condition(); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return false; }
    }

    [Fact]
    public void PostToolUseEditThroughTheCliReindexesTheMemoryChannel()
    {
        StartServer();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string memoryDir = Path.Combine(home, ".claude", "projects", $"fixture-{_instance}", "memory");
        Directory.CreateDirectory(memoryDir);
        try
        {
            string editedFile = Path.Combine(memoryDir, "topic-example.md");
            File.WriteAllText(editedFile, "---\nname: Example topic\n---\nSome real body text about a hard rule.\n");
            GrimoiraCliRunner.Run($"init --instance {_instance}");
            string payload = JsonSerializer.Serialize(new { cwd = _projectDir, session_id = "s3", tool_name = "Edit", tool_input = new { file_path = editedFile } });

            (int exit, string stdout, _) = RunHook("PostToolUse", payload);

            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Equal(1L, Scalar("SELECT count(*) FROM memory"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(memoryDir)!, recursive: true);
        }
    }

    // Windows answers a connect to a closed loopback port only after about 3 s of SYN retries, so a CLI that
    // simply waits for the refusal would hold every session end that long. SessionEnd hooks share a 1.5 s budget.
    [Theory]
    [InlineData("SessionEnd")]
    [InlineData("PostToolUse")]
    [InlineData("PreCompact")]
    [InlineData("UserPromptSubmit")]
    public void WithTheServerDownTheHookExitsZeroFast(string eventName)
    {
        string payload = JsonSerializer.Serialize(new { cwd = _projectDir, session_id = "s4", transcript_path = WriteTranscript(), tool_name = "Edit", tool_input = new { file_path = Path.Combine(_projectDir, "x.md") } });
        RunHook(eventName, payload);

        (int exit, _, long elapsedMs) = RunHook(eventName, payload);

        _output.WriteLine($"{eventName}, server down: {elapsedMs} ms");
        Assert.Equal(0, exit);
        Assert.True(elapsedMs < 2000, $"{eventName} took {elapsedMs} ms with the server down");
    }

    // Each call is measured against the timeout its slot declares in hooks.json, 5 warm runs per event.
    [Theory]
    [InlineData("PreCompact")]
    [InlineData("UserPromptSubmit")]
    [InlineData("SessionEnd")]
    public void EachHookCallStaysWellUnderItsSlotTimeout(string eventName)
    {
        StartServer();
        GrimoiraCliRunner.Run($"init --instance {_instance}");
        string transcript = WriteTranscript();
        int timeoutSeconds = SlotTimeoutSeconds(eventName);
        List<long> runs = [];
        for (int i = 0; i < 6; i++)
        {
            string payload = JsonSerializer.Serialize(new { cwd = _projectDir, session_id = $"t{i}", transcript_path = transcript, prompt = "go on" });
            (int exit, _, long elapsedMs) = RunHook(eventName, payload);
            Assert.Equal(0, exit);
            if (i > 0) runs.Add(elapsedMs);
        }

        _output.WriteLine($"{eventName}: {string.Join(", ", runs)} ms; max {runs.Max()} ms; slot timeout {timeoutSeconds} s");
        Assert.True(runs.Max() < timeoutSeconds * 1000 / 4, $"{eventName} max {runs.Max()} ms against a {timeoutSeconds} s timeout");
    }

    private static int SlotTimeoutSeconds(string eventName)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        return doc.RootElement.GetProperty("hooks").GetProperty(eventName)[0].GetProperty("hooks")[0].GetProperty("timeout").GetInt32();
    }

    private string WriteTranscript()
    {
        string transcriptPath = Path.Combine(_projectDir, "t.jsonl");
        File.WriteAllLines(transcriptPath,
        [
            JsonSerializer.Serialize(new { type = "user", uuid = "u1", timestamp = "t1", message = new { content = "Ship the hook slots on the published CLI." } }),
        ]);
        return transcriptPath;
    }

    private void StartServer()
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(ServerDll);
        psi.Environment["GRIMOIRA_DATA_DIR"] = _dataDir;
        psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        psi.Environment.Remove("GRIMOIRA_INSTANCE");
        Process process = Process.Start(psi)!;
        _servers.Add(process);
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            try
            {
                using HttpClient client = PipeTestClient.CreateClient(_dataDir, TimeSpan.FromSeconds(1));
                if (client.GetAsync("/health").Result.IsSuccessStatusCode) return;
            }
            catch (Exception) when (!process.HasExited) { }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("the test server never answered /health");
    }

    // `grimoira hook <event>` as a command hook runs it: payload on stdin, CLAUDE_PROJECT_DIR set the way Claude
    // Code sets it, and the data dir of this test's server (no server listens when none was started).
    private (int Exit, string Stdout, long ElapsedMs) RunHook(string eventName, string payload)
    {
        Assert.True(File.Exists(CliDll), $"grimoira not built at {CliDll}");
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(CliDll);
        psi.ArgumentList.Add("hook");
        psi.ArgumentList.Add(eventName);
        psi.Environment["GRIMOIRA_DATA_DIR"] = _dataDir;
        psi.Environment["GRIMOIRA_SERVER_EXE"] = Path.Combine(_dataDir, "missing", "Grimoira.Server.exe");
        psi.Environment["CLAUDE_PROJECT_DIR"] = _projectDir;
        psi.Environment.Remove("GRIMOIRA_INSTANCE");
        Stopwatch sw = Stopwatch.StartNew();
        using Process p = Process.Start(psi)!;
        p.StandardInput.Write(payload);
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"grimoira hook {eventName} did not exit within 60 s");
        }
        long elapsedMs = sw.ElapsedMilliseconds;
        if (stderr.Result.Length > 0) _output.WriteLine($"stderr of hook {eventName}: {stderr.Result}");
        return (p.ExitCode, stdout.Result, elapsedMs);
    }

    private long Scalar(string sql)
    {
        using SqliteConnection connection = new($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        foreach (Process p in _servers)
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } } catch (Exception) { }
            p.Dispose();
        }
        SqliteConnection.ClearAllPools();
        GrimoiraCliRunner.DeleteInstance(_instance);
        Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", _previousDataDirEnv);
        try { Directory.Delete(_dataDir, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_projectDir, recursive: true); } catch (Exception) { }
    }
}
