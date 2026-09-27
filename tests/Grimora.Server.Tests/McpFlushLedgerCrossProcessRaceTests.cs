using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Server.Tests;

/// <summary>
/// <see cref="McpFlushLedgerRaceTests"/> proves the in-process <c>LedgerGate</c> serializes two
/// <c>tools/call</c> dispatched concurrently by ONE <c>mcp.dll</c> process. That gate is a per-process
/// <c>SemaphoreSlim</c> (a <c>ConcurrentDictionary</c> keyed by path, held only for the life of that one
/// process) — it cannot serialize a second, independent <c>mcp.dll</c> process racing the SAME instance's
/// <c>pending-learn.jsonl</c>, which is exactly what happens whenever two Claude Code sessions (or an MCP
/// session plus a CLI hook) point at the same instance at once. This test spawns two REAL, separate
/// <c>dotnet bin/mcp.dll</c> processes and drives them at the same instance concurrently, round after
/// round: each round, both processes stage a distinct learning and flush at (as near as the OS scheduler
/// allows) the same moment. Before the cross-process ledger file lock, this reproduces the same failure
/// modes as the single-process test — a duplicate apply (<c>UNIQUE constraint failed</c>), a lost line
/// (staged but never delivered and no longer on disk), or a crashed call (an unhandled exception surfaced
/// to the client as "An error occurred invoking 'brain_flush'") — because nothing outside one process's
/// memory stops the two OS processes from interleaving their reads and writes of the same file.
/// </summary>
public sealed class McpFlushLedgerCrossProcessRaceTests
{
    private const int Rounds = 15;

    [Fact]
    public async Task TwoRealProcessesRacingStageAndFlushApplyEveryLineExactlyOnce()
    {
        string mcpDll = FindBinMcpDll();
        string instance = GrimoraCliRunner.NewTestInstance("cross-process-race");
        List<string> violations = [];
        int expectedApplied = 0;
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            for (int round = 0; round < Rounds; round++)
            {
                string keyA = $"cpr-{round}-a";
                string keyB = $"cpr-{round}-b";
                expectedApplied += 2;

                Task<(IReadOnlyList<string> toolNames, IReadOnlyList<string> results)> taskA = Task.Run(() =>
                    McpProcess.Run(mcpDll, instance,
                    [
                        ("brain_stage", new { kind = "node", key = keyA, a = "concept", b = "Race A", c = "", because = "", hard = false }),
                        ("brain_flush", new { }),
                    ]));
                Task<(IReadOnlyList<string> toolNames, IReadOnlyList<string> results)> taskB = Task.Run(() =>
                    McpProcess.Run(mcpDll, instance,
                    [
                        ("brain_stage", new { kind = "node", key = keyB, a = "concept", b = "Race B", c = "", because = "", hard = false }),
                        ("brain_flush", new { }),
                    ]));

                (IReadOnlyList<string> toolNames, IReadOnlyList<string> results)[] outcomes = await Task.WhenAll(taskA, taskB);

                CheckRound(round, "A", outcomes[0].results, violations);
                CheckRound(round, "B", outcomes[1].results, violations);
            }

            // Drain: a line legitimately kept for retry after a transient DB-lock reject is still on disk,
            // never lost — one more flush must deliver everything still outstanding.
            McpProcess.Run(mcpDll, instance, [("brain_flush", new { })]);

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
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static void CheckRound(int round, string label, IReadOnlyList<string> results, List<string> violations)
    {
        string stageResult = results[0];
        string flushResult = results[1];
        if (!stageResult.StartsWith("staged node", StringComparison.Ordinal))
            violations.Add($"[{round}{label}] brain_stage did not confirm staging: {stageResult}");
        if (flushResult.Contains("UNIQUE constraint failed", StringComparison.Ordinal))
            violations.Add($"[{round}{label}] flush applied a line twice: {flushResult}");
        if (flushResult.Contains("error occurred", StringComparison.OrdinalIgnoreCase))
            violations.Add($"[{round}{label}] flush crashed: {flushResult}");
    }

    private static int CountRaceNodes(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM node WHERE k LIKE 'cpr-%' AND valid_to IS NULL";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string FindBinMcpDll()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "bin", "mcp.dll");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("bin/mcp.dll not found — run build-mcp.ps1 first");
    }
}
