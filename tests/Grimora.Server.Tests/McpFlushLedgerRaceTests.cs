using Grimora.TestSupport;
using Xunit;

namespace Grimora.Server.Tests;

/// <summary>
/// Reproduces, against the real compiled <c>bin/mcp.dll</c> over stdio, the timing hole this slice
/// investigates: a client is free to pipeline <c>brain_stage</c> then <c>brain_flush</c> then a second
/// <c>brain_flush</c> without waiting for each reply (<see cref="McpProcess.Run"/> sends every call before
/// any reply arrives, the same shape a real client using request pipelining would produce), and the MCP
/// host dispatches each <c>tools/call</c> concurrently rather than one at a time. Before the
/// <c>LedgerGate</c> fix (<c>Grimora.Brain.Tools.BrainStageTool.cs</c>), two such calls racing the same
/// <c>pending-learn.jsonl</c> could apply the same staged line twice — a
/// <c>UNIQUE constraint failed: node.k</c> reject reported as "flushed 0 learning(s)" — or crash one of the
/// calls outright (a flush's read racing another flush's delete, surfaced to the client as
/// "An error occurred invoking 'brain_flush'"). Neither may ever happen now: each of the two flush replies
/// must be a clean "nothing staged." or a clean "flushed 1 learning(s) into the brain.", nothing else.
/// </summary>
public sealed class McpFlushLedgerRaceTests
{
    private const int Repetitions = 30;

    [Fact]
    public void PipelinedStageThenTwoFlushesNeverCorruptOrCrash()
    {
        string mcpDll = FindBinMcpDll();
        List<string> violations = [];

        for (int i = 0; i < Repetitions; i++)
        {
            string instance = GrimoraCliRunner.NewTestInstance($"flush-race-{i}");
            try
            {
                GrimoraCliRunner.Run($"init --instance {instance}");
                (_, IReadOnlyList<string> results) = McpProcess.Run(mcpDll, instance,
                [
                    ("brain_stage", new { kind = "node", key = $"flush-race-node-{i}", a = "concept", b = "Race Label", c = "", because = "", hard = false }),
                    ("brain_flush", new { }),
                    ("brain_flush", new { }),
                ]);

                string stageResult = results[0];
                string flush1 = results[1];
                string flush2 = results[2];

                if (!stageResult.StartsWith("staged node", StringComparison.Ordinal))
                    violations.Add($"[{i}] brain_stage did not confirm staging: {stageResult}");
                foreach ((string label, string flushResult) in new[] { ("flush1", flush1), ("flush2", flush2) })
                {
                    bool clean = flushResult.StartsWith("nothing staged.", StringComparison.Ordinal)
                        || flushResult == "flushed 1 learning(s) into the brain."
                        || flushResult == "flushed 0 learning(s) into the brain.";
                    if (!clean || flushResult.Contains("UNIQUE constraint failed", StringComparison.Ordinal))
                        violations.Add($"[{i}] {label} was not a clean flush reply: {flushResult}");
                }

                // The staged learning must never vanish: exactly one of "a flush reported it delivered"
                // or "it is still on disk waiting for the next flush" must hold.
                bool oneFlushDelivered = flush1.StartsWith("flushed 1", StringComparison.Ordinal)
                    || flush2.StartsWith("flushed 1", StringComparison.Ordinal);
                string ledger = Path.Combine(GrimoraCliRunner.InstanceDir(instance), "pending-learn.jsonl");
                bool stillStaged = File.Exists(ledger) && File.ReadAllText(ledger).Contains($"flush-race-node-{i}", StringComparison.Ordinal);
                if (!oneFlushDelivered && !stillStaged)
                    violations.Add($"[{i}] the staged learning was neither delivered nor left in the ledger — it was lost (flush1={flush1}, flush2={flush2})");
            }
            finally
            {
                GrimoraCliRunner.DeleteInstance(instance);
            }
        }

        Assert.True(violations.Count == 0, $"{violations.Count} of {Repetitions} runs raced the ledger:\n" + string.Join("\n", violations));
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
