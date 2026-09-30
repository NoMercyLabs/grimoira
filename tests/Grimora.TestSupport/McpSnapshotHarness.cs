using System.Diagnostics;
using System.Text.Json;

namespace Grimora.TestSupport;

/// <summary>
/// Runs an MCP server dll (JSON-RPC over stdio, the shape Claude Code itself speaks) and returns its
/// answers. Shared by the "snapshot vs today" tests that prove a mcp.cs rewrite (RESTRUCTURE.md slice
/// 24, and its later MCP part) changed nothing a client can see: same <c>tools/list</c>, same text back
/// for the same call. Same JSON-RPC shape as mcp-graph.test.mjs/mcp-stage.test.mjs, in C# so the .NET
/// test run (verify.ps1's <c>dotnet test</c>) can assert it without a Node dependency.
/// </summary>
public static class McpProcess
{
    /// <summary>Starts <c>dotnet &lt;dllPath&gt;</c> with the given instance, sends <c>tools/list</c> and
    /// then each call in order, and returns the tool names and each call's result text, in order.
    /// By default each call goes out only after the previous reply arrived, the way an awaiting client
    /// (Claude Code, McpClient) drives a server; the pinned pre-slice-24 oracle has no lock around its
    /// <c>pending-learn.jsonl</c> read-then-delete, so two <c>brain_flush</c> calls sent back to back can
    /// throw inside it. <paramref name="pipelined"/> sends every call up front, for the tests that
    /// deliberately race today's <c>bin/mcp.dll</c> (McpFlushLedgerRaceTests).</summary>
    public static (IReadOnlyList<string> toolNames, IReadOnlyList<string> results) Run(
        string dllPath, string instance, IReadOnlyList<(string Name, object Args)> calls, int timeoutMs = 15000, bool pipelined = false)
    {
        ProcessStartInfo psi = new("dotnet", $"\"{dllPath}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            Environment = { ["GRIMORA_INSTANCE"] = instance, ["AITM_INSTANCE"] = instance },
        };

        // The pinned snapshot still keeps its store under ~/.aitm; hand it the instance and take it back.
        bool snapshot = dllPath.Contains("grimora-mcp-snapshot", StringComparison.Ordinal);
        if (snapshot) OldStore.ToOld(instance);
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start dotnet {dllPath}");

        List<string> lines = [];
        object gate = new();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (gate) lines.Add(e.Data);
        };
        process.BeginOutputReadLine();

        Send(process, new { jsonrpc = "2.0", id = 0, method = "initialize", @params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "test", version = "1" } } });
        Send(process, new { jsonrpc = "2.0", method = "notifications/initialized" });
        Send(process, new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } });
        Stopwatch budget = Stopwatch.StartNew();
        int Remaining() => (int)Math.Max(0, timeoutMs - budget.ElapsedMilliseconds);
        for (int i = 0; i < calls.Count; i++)
        {
            Send(process, new { jsonrpc = "2.0", id = 100 + i, method = "tools/call", @params = new { name = calls[i].Name, arguments = calls[i].Args } });
            // A reply that never comes ends the wait at the shared budget; a missing line then surfaces as
            // a clear assertion failure downstream rather than a hang.
            if (!pipelined && !SpinWait(() => CallsComplete(lines, gate, i + 1), Remaining())) break;
        }

        SpinWait(() => Snapshot(lines, gate).Any(l => ContainsId(l, 1)) && CallsComplete(lines, gate, calls.Count), Remaining());

        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        process.WaitForExit(2000);
        if (snapshot) OldStore.FromOld(instance);

        List<string> allLines = Snapshot(lines, gate);
        List<string> toolNames = [];
        JsonElement? listResult = FindResult(allLines, 1);
        if (listResult is { } list && list.TryGetProperty("tools", out JsonElement tools))
            foreach (JsonElement tool in tools.EnumerateArray())
                toolNames.Add(tool.GetProperty("name").GetString()!);

        List<string> results = [];
        for (int i = 0; i < calls.Count; i++)
        {
            JsonElement? result = FindResult(allLines, 100 + i);
            results.Add(result is { } r && r.TryGetProperty("content", out JsonElement content) && content.GetArrayLength() > 0
                ? content[0].GetProperty("text").GetString() ?? ""
                : "");
        }
        return (toolNames, results);
    }

    private static bool CallsComplete(List<string> lines, object gate, int callCount)
    {
        List<string> snap = Snapshot(lines, gate);
        for (int i = 0; i < callCount; i++)
            if (!snap.Any(l => ContainsId(l, 100 + i))) return false;
        return true;
    }

    private static List<string> Snapshot(List<string> lines, object gate) { lock (gate) return [.. lines]; }

    private static bool ContainsId(string line, int id)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.GetInt32() == id;
        }
        catch (JsonException) { return false; }
    }

    private static JsonElement? FindResult(List<string> lines, int id)
    {
        foreach (string line in lines)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number
                    && idEl.GetInt32() == id && doc.RootElement.TryGetProperty("result", out JsonElement result))
                    return result.Clone();
            }
            catch (JsonException) { /* not a JSON-RPC line, or not this one */ }
        }
        return null;
    }

    private static void Send(Process process, object message) =>
        process.StandardInput.WriteLine(JsonSerializer.Serialize(message));

    private static bool SpinWait(Func<bool> condition, int timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}

/// <summary>
/// Builds the pinned pre-slice-24 <c>mcp.cs</c> (the file this slice's cards rewrite) into its own
/// snapshot directory once per process, so a parity test can run it as the "before" oracle without
/// disturbing the repo's own <c>bin/mcp.dll</c> (built by <c>build-mcp.ps1</c> from the edited source).
/// </summary>
public static class McpSnapshotHarness
{
    // The commit this worktree branched from (origin/master, before any slice-24 mcp.cs edit) —
    // mcp.cs there is the pre-dispatch oracle every wired tool must still match byte for byte.
    public const string PreSlice24Commit = "52968343e93812c133419702de1b7a9f417db86d";

    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    /// <summary>Returns the path to the snapshot's <c>mcp.dll</c>, building it on first use.</summary>
    public static string EnsureBuilt(string repoRoot, string commit = PreSlice24Commit)
    {
        string snapshotDir = Path.Combine(Path.GetTempPath(), $"grimora-mcp-snapshot-{commit}");
        string dll = Path.Combine(snapshotDir, "mcp.dll");
        string stamp = Path.Combine(snapshotDir, ".built-ok");
        if (File.Exists(dll) && File.Exists(stamp)) return dll;

        BuildLock.Wait();
        try
        {
            if (File.Exists(dll) && File.Exists(stamp)) return dll;

            // Another test run (a parallel agent in another worktree) may build the same snapshot at
            // the same time. Build in a folder of our own and move it into place in one step, so no run
            // ever reads a half-built snapshot. The loser of the race uses the winner's copy.
            string finalDir = snapshotDir;
            snapshotDir = $"{finalDir}.building-{Environment.ProcessId}-{Guid.NewGuid():N}";
            dll = Path.Combine(snapshotDir, "mcp.dll");
            stamp = Path.Combine(snapshotDir, ".built-ok");
            Directory.CreateDirectory(snapshotDir);

            string source = RunGit(repoRoot, $"show {commit}:mcp.cs");
            string sourceCopyDir = Path.Combine(snapshotDir, "src");
            Directory.CreateDirectory(sourceCopyDir);
            string sourceCopy = Path.Combine(sourceCopyDir, "mcp.cs");
            File.WriteAllText(sourceCopy, source);

            ProcessStartInfo psi = new("dotnet", $"build \"{sourceCopy}\" -c Release -o \"{snapshotDir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = repoRoot,
            };
            using Process build = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet build");
            string stdout = build.StandardOutput.ReadToEnd();
            string stderr = build.StandardError.ReadToEnd();
            build.WaitForExit();
            if (build.ExitCode != 0 || !File.Exists(dll))
                throw new InvalidOperationException($"snapshot build of mcp.cs@{commit} failed:\n{stdout}\n{stderr}");

            File.WriteAllText(stamp, DateTime.UtcNow.ToString("o"));
            return MoveIntoPlace(snapshotDir, finalDir);
        }
        finally
        {
            BuildLock.Release();
        }
    }

    private static string MoveIntoPlace(string builtDir, string finalDir)
    {
        string finalDll = Path.Combine(finalDir, "mcp.dll");
        string finalStamp = Path.Combine(finalDir, ".built-ok");
        // A final folder without the stamp is a leftover of the old in-place build; builds now happen
        // only in private folders, so nobody else is writing it.
        if (Directory.Exists(finalDir) && !File.Exists(finalStamp)) TryDelete(finalDir);
        try
        {
            Directory.Move(builtDir, finalDir);
        }
        catch (IOException) when (File.Exists(finalStamp))
        {
            TryDelete(builtDir); // another run won the race; its copy is complete
        }
        return finalDll;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string RunGit(string repoRoot, string arguments)
    {
        ProcessStartInfo psi = new("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments} failed: {stderr}");
        return stdout;
    }
}

/// <summary>
/// Builds <c>fake-mcp-server.cs</c> (copied beside this assembly) once per process: a stand-in stdio
/// server whose every <c>tools/call</c> reply names the request ids that had already arrived. It exists
/// to prove how <see cref="McpProcess.Run"/> drives a server: one call at a time, each sent only after
/// the previous reply, the way an awaiting client (Claude Code, McpClient) does. The pinned pre-slice-24
/// mcp.dll oracle has no synchronization around <c>pending-learn.jsonl</c>, so a driver that pipelined two
/// <c>brain_flush</c> calls at it raced its own read-then-delete and, once in about 20 full runs, got
/// "An error occurred invoking 'brain_flush'." back instead of a real answer.
/// </summary>
public static class FakeMcpServer
{
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    public static string EnsureBuilt()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "fake-mcp-server.cs");
        string outDir = Path.Combine(Path.GetTempPath(), $"grimora-fake-mcp-{Environment.ProcessId}");
        string dll = Path.Combine(outDir, "fake-mcp-server.dll");
        if (File.Exists(dll)) return dll;
        BuildLock.Wait();
        try
        {
            if (File.Exists(dll)) return dll;
            ProcessStartInfo psi = new("dotnet", $"build \"{source}\" -c Release -o \"{outDir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using Process build = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet build");
            string stdout = build.StandardOutput.ReadToEnd();
            string stderr = build.StandardError.ReadToEnd();
            build.WaitForExit();
            if (build.ExitCode != 0 || !File.Exists(dll))
                throw new InvalidOperationException($"build of fake-mcp-server.cs failed:/n{stdout}/n{stderr}");
            return dll;
        }
        finally
        {
            BuildLock.Release();
        }
    }
}
