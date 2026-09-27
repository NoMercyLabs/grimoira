using System.Diagnostics;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Store.Tests;

public class StatsToolTests
{
    [Fact]
    public void MatchesTodaysCliOutput()
    {
        string instance = GrimoraCliRunner.NewTestInstance("stats");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"add --instance {instance} --term fixture-one --value one --category manual");
            GrimoraCliRunner.Run($"add --instance {instance} --term fixture-two --value two --category manual");
            GrimoraCliRunner.Run($"todo --instance {instance} --title open-item");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"stats --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new StatsTool().Execute(connection, instance, dbPath));

            Assert.Equal(expected, actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 3: "tok.cs becomes stats --tokens: same output, test first (old
    // tok.cs output vs new, on a fixture)." tok.cs is a standalone file-based app (never a case in
    // grimora.cs's switch), so its oracle is `dotnet run tok.cs <instance>` directly, on the same fixture
    // store the new `stats --tokens` (StatsTool.ExecuteTokens) reads.
    [Fact]
    public void TokensFlagMatchesTheOldStandaloneTokCsOutputOnAFixtureStore()
    {
        string instance = GrimoraCliRunner.NewTestInstance("stats-tokens");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            GrimoraCliRunner.Run($"add --instance {instance} --term tokens-fixture --value \"a short verified fact used to measure token cost\" --category manual");
            GrimoraCliRunner.Run($"index-docs --instance {instance} --from \"{MakeFixtureDoc()}\"");

            string expected = RunOldTokCs(instance);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new StatsTool().ExecuteTokens(connection, instance);

            Assert.Equal(Normalize(StripBuildNoise(expected)), Normalize(actual));
            Assert.Contains("token cost (o200k_base)", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureDoc()
    {
        string path = Path.Combine(Path.GetTempPath(), $"grimora-stats-tokens-fixture-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "# Tokens Fixture\n\nSome body text used only to measure the token cost of a doc section.\n");
        return path;
    }

    // tok.cs is deleted in the same slice that adds `stats --tokens` (RESTRUCTURE.md: "tok.cs: moves
    // into Store as `stats --tokens`"), so the oracle can't run the live file the way other pinned tests
    // do — it reads the last commit that still had it, the same git-show pattern
    // CliFlagCoverageGuardTests.ReadOracleSource already uses, but written to a temp file so `dotnet
    // run` can execute it (not just read its text).
    private const string TokCsOracleCommit = "07a021d722108d838d364ac854fa28ae6b8a116a";

    private static string RunOldTokCs(string instance)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"grimora-tok-oracle-{Guid.NewGuid():N}.cs");
        File.WriteAllText(tempFile, ReadTokCsAtOracleCommit());
        OldStore.ToOld(instance); // the pinned tok.cs reads the old store folder
        try
        {
            ProcessStartInfo psi = new("dotnet", $"run \"{tempFile}\" {instance}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet run tok.cs");
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"tok.cs exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
            return stdout;
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static string ReadTokCsAtOracleCommit()
    {
        ProcessStartInfo psi = new("git", $"-C \"{FindRepoRoot()}\" show {TokCsOracleCommit}:tok.cs")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("could not start git show");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git show {TokCsOracleCommit}:tok.cs exited {process.ExitCode}");
        return stdout;
    }

    // This test assembly runs from tests/Grimora.Store.Tests/bin/.../, so walk up to find the repo's .git.
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath)) return dir.FullName; // worktree checkouts have a .git file, not a directory
            dir = dir.Parent;
        }
        throw new InvalidOperationException($".git not found above {AppContext.BaseDirectory}");
    }

    // A file-based app run from a fresh temp path restores on every call (no cached obj/), and that
    // restore's own diagnostics (unrelated NuGet advisory warnings) land on the SAME stdout `dotnet run`
    // uses for the app's own output — this drops everything before the tool's actual first line.
    private static string StripBuildNoise(string s)
    {
        int start = s.IndexOf("instance '", StringComparison.Ordinal);
        return start >= 0 ? s[start..] : s;
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
