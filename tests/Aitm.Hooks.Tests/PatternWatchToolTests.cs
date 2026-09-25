using System.Text.Json;
using Aitm.Hooks.Data;
using Aitm.Hooks.Tools;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Hooks.Tests;

/// <summary>
/// RESTRUCTURE.md slice 22 ("Hooks, part 3"): pattern-watch.mjs moves in as a record-only PostToolUse
/// handler (docs/RESTRUCTURE.md:268) — it keeps counting command shapes but no longer prints a nudge
/// into the session. <see cref="PatternWatchTool.Signature"/> ports every case in
/// pattern-watch.test.mjs verbatim (seven real signature bugs, each caught by one of these cases); the
/// remaining tests here pin the recording contract the card asks for: a matching event writes a row, a
/// non-matching one writes nothing, and a bad payload or a locked store must exit clean with no output
/// (the fail-open rule every other Hooks handler in this project already keeps).
/// </summary>
public class PatternWatchToolTests
{
    // --- signature(): ported from pattern-watch.test.mjs, one case per line there ---

    [Theory]
    [InlineData("cd C:/Projects/aitm && node brain-lib.test.mjs", null)]
    [InlineData("Set-Location C:/Projects/aitm; ./build-cli.ps1", null)]
    [InlineData("for q in a b; do ./bin-cli/aitm.exe doc \"$q\"; done", "aitm doc")]
    [InlineData("./build.ps1 -Quick 2>&1", null)]
    [InlineData("\"C:/Program Files/nodejs/node.exe\" index-code.mjs", null)]
    [InlineData("npm run build", "npm run build")]
    [InlineData("yarn run test:unit", "yarn run test:unit")]
    [InlineData("node continue-guard.test.mjs", null)]
    [InlineData("python scripts/gradle-gate.py", null)]
    [InlineData("git rm --cached x", null)]
    [InlineData("git status --short", null)]
    [InlineData("grep -rn foo src", null)]
    [InlineData("r=$(curl -s -m 2 http://127.0.0.1:9222/json/version)", null)]
    [InlineData("$p = Get-CimInstance Win32_Process -Filter \"x\"", "get-ciminstance win32_process")]
    [InlineData("adb -s emulator-5560 shell input keyevent 4", "adb shell")]
    [InlineData("sleep 5", null)]
    [InlineData("gh run watch 123 --exit-status", "gh run")]
    [InlineData("dotnet build aitm.cs -c Release", "dotnet build")]
    [InlineData("git commit -q -m \"x\"", "git commit")]
    public void SignatureMatchesTheMjsPort(string command, string? expected)
    {
        Assert.Equal(expected, PatternWatchTool.Signature(command));
    }

    // --- Execute(): the record-only contract ---

    private static string NewTempProjectDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"test-hooks-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static long PatternCount(string instance, string sig)
    {
        using SqliteConnection connection = new($"Data Source={HookPaths.DbPath(instance)};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count FROM patterns WHERE sig = $sig";
        cmd.Parameters.AddWithValue("$sig", sig);
        object? result = cmd.ExecuteScalar();
        return result is null ? 0 : Convert.ToInt64(result);
    }

    [Fact]
    public void AMatchingCommandWritesARecord()
    {
        string projectDir = NewTempProjectDir("watch-ok");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new
            {
                tool_input = new { command = "dotnet build aitm.cs -c Release" },
                cwd = projectDir,
                session_id = "s1",
            });

            string result = PatternWatchTool.Execute(payload);

            Assert.Equal("", result);
            Assert.Equal(1, PatternCount(instance, "dotnet build"));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void ANonMatchingCommandWritesNothing()
    {
        string projectDir = NewTempProjectDir("watch-nomatch");
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string payload = JsonSerializer.Serialize(new
            {
                tool_input = new { command = "sleep 5" },
                cwd = projectDir,
                session_id = "s1",
            });

            string result = PatternWatchTool.Execute(payload);

            Assert.Equal("", result);
            using SqliteConnection connection = new($"Data Source={HookPaths.DbPath(instance)};Mode=ReadOnly");
            connection.Open();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM patterns";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void ABadPayloadExitsCleanWithNoOutput()
    {
        string result = PatternWatchTool.Execute("{ not json");
        Assert.Equal("", result);
    }

    [Fact]
    public void ALockedStoreExitsCleanWithNoOutput()
    {
        string projectDir = NewTempProjectDir("watch-locked");
        string instance = HookPaths.ResolveInstance(projectDir);
        SqliteConnection? locker = null;
        SqliteTransaction? lockTx = null;
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = HookPaths.DbPath(instance);
            locker = new SqliteConnection($"Data Source={dbPath}");
            locker.Open();
            lockTx = locker.BeginTransaction();
            using SqliteCommand hold = locker.CreateCommand();
            hold.Transaction = lockTx;
            hold.CommandText = "INSERT INTO meta(key,value) VALUES('pattern-watch-lock-test','1')";
            hold.ExecuteNonQuery();

            string payload = JsonSerializer.Serialize(new
            {
                tool_input = new { command = "dotnet build aitm.cs -c Release" },
                cwd = projectDir,
                session_id = "s1",
            });

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            string result = PatternWatchTool.Execute(payload);
            sw.Stop();

            Assert.Equal("", result);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed} — must fail open fast, not wait out the store's usual 30s lock timeout");
        }
        finally
        {
            lockTx?.Rollback();
            locker?.Dispose();
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
