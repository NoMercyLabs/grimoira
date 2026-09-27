using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

/// <summary>
/// The CLI verbs <c>grimora stage</c> and <c>grimora flush</c> (grimora.cs's <c>case "stage"</c>/<c>case
/// "flush"</c>) each run as a brand-new, short-lived <c>dotnet bin-cli/grimora.dll</c> process — one process
/// per invocation, the shape a hook or a script actually uses them in. <see cref="BrainStageTool"/> and
/// <see cref="BrainFlushTool"/>'s in-process <c>LedgerGate</c> (see their MCP-path tests) cannot help here:
/// a <c>SemaphoreSlim</c> keyed in a <c>ConcurrentDictionary</c> lives inside one process's memory and is
/// gone the moment that process exits, so two <c>grimora stage</c>/<c>grimora flush</c> processes racing the SAME
/// instance's <c>pending-learn.jsonl</c> — e.g. two hooks firing back to back, or a CLI stage racing an MCP
/// flush on the same instance — have nothing in common to serialize on except the file itself. This test
/// spawns two REAL <c>dotnet bin-cli/grimora.dll</c> processes per round and drives them at the same instance
/// concurrently: each stages a distinct learning and immediately flushes. Before the cross-process ledger
/// file lock, this can duplicate-apply a line (<c>UNIQUE constraint failed</c>), lose one (staged, dropped
/// by a racing delete, never delivered), or throw (a flush's read racing another flush's delete).
/// </summary>
public sealed class LedgerCrossProcessCliRaceTests
{
    private const int Rounds = 15;

    [Fact]
    public async Task TwoRealCliProcessesRacingStageAndFlushApplyEveryLineExactlyOnce()
    {
        string instance = GrimoraCliRunner.NewTestInstance("cli-cross-process-race");
        List<string> violations = [];
        int expectedApplied = 0;
        try
        {
            GrimoraCliRunner.RunBinCli($"init --instance {instance}");

            for (int round = 0; round < Rounds; round++)
            {
                string keyA = $"cpcr-{round}-a";
                string keyB = $"cpcr-{round}-b";
                expectedApplied += 2;

                Task<(string stdout, int exitCode)> taskA = Task.Run(() => RunStageThenFlush(instance, keyA));
                Task<(string stdout, int exitCode)> taskB = Task.Run(() => RunStageThenFlush(instance, keyB));
                (string stdout, int exitCode)[] outcomes = await Task.WhenAll(taskA, taskB);

                Check(round, "A", outcomes[0], violations);
                Check(round, "B", outcomes[1], violations);
            }

            // Drain: any line kept for retry after a transient DB-lock reject is still on disk, never
            // lost — one more flush must deliver everything still outstanding.
            GrimoraCliRunner.RunBinCli($"flush --instance {instance}");

            string ledger = Path.Combine(GrimoraCliRunner.InstanceDir(instance), "pending-learn.jsonl");
            Assert.False(
                File.Exists(ledger) && File.ReadAllText(ledger).Trim().Length > 0,
                $"ledger still has staged lines after the drain flush: {(File.Exists(ledger) ? File.ReadAllText(ledger) : "")}");

            Assert.True(violations.Count == 0, $"{violations.Count} violation(s):\n" + string.Join("\n", violations));

            int actualApplied = CountRaceNodes(GrimoraCliRunner.InstanceDbPath(instance));
            Assert.Equal(expectedApplied, actualApplied);
        }
        finally
        {
            // Real bin-cli calls spawn a background grimora server that keeps the db file open until it goes
            // idle (Program.cs's IdleExit), well past this test's own cleanup — same situation
            // SweepStaleInstances already handles for a leaked store, so leave it for that sweep too.
            try { GrimoraCliRunner.DeleteInstance(instance); }
            catch (IOException) { /* server still holds the file; the next run's SweepStaleInstances collects it */ }
        }
    }

    private static (string stdout, int exitCode) RunStageThenFlush(string instance, string key)
    {
        (string stageOut, int stageExit) = GrimoraCliRunner.RunBinCli($"stage node {key} concept \"Race Label\" --instance {instance}");
        (string flushOut, int flushExit) = GrimoraCliRunner.RunBinCli($"flush --instance {instance}");
        return ($"stage: {stageOut.Trim()} | flush: {flushOut.Trim()}", stageExit != 0 ? stageExit : flushExit);
    }

    private static void Check(int round, string label, (string stdout, int exitCode) result, List<string> violations)
    {
        if (result.exitCode != 0)
            violations.Add($"[{round}{label}] non-zero exit ({result.exitCode}): {result.stdout}");
        if (!result.stdout.Contains("staged node", StringComparison.Ordinal))
            violations.Add($"[{round}{label}] stage did not confirm staging: {result.stdout}");
        if (result.stdout.Contains("UNIQUE constraint failed", StringComparison.Ordinal))
            violations.Add($"[{round}{label}] flush applied a line twice: {result.stdout}");
    }

    private static int CountRaceNodes(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM node WHERE k LIKE 'cpcr-%' AND valid_to IS NULL";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
