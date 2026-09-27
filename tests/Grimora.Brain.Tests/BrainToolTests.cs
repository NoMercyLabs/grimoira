using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainToolTests
{
    [Fact]
    public void RouterDispatchesCoreToTheSameOutputAsTheStandaloneTool()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-router-core");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            GrimoraCliRunner.Run($"init --instance {instance}");
            BrainTestFixtures.InsertHardNode(dbPath, "rule:router", "rule", "Routed", "via brain core");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string viaRouter = new BrainTool().ExecuteCli(connection, ["core"]);
            string viaTool = new BrainCoreTool().ExecuteCli(connection);

            Assert.Equal(viaTool, viaRouter);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void UnknownSubVerbMatchesTodaysCliExitCodeAndMessage()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-router-unknown");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            // Oracle: today's grimora.cs BrainCmd default case (grimora.cs:1354-1357).
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain --instance {instance} not-a-real-subverb");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainTool().ExecuteCli(connection, ["not-a-real-subverb"]).Trim();

            AssertSameMessageModuloConsoleCodepage(expected, actual);
            Assert.Equal(
                "brain <core|scope <proj…>|common <proj…>|place <codekind>|recall <text>|impact <symbol>|learn …|gaps|distill|stats>",
                new BrainTool().Help);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void NoSubVerbMatchesTodaysCliExitCodeAndMessage()
    {
        string instance = GrimoraCliRunner.NewTestInstance("brain-router-empty");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            // "brain" with no sub-verb defaults to sub == "help" in grimora.cs's BrainCmd (grimora.cs:1305),
            // which also falls into the default case.
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainTool().ExecuteCli(connection, []).Trim();

            AssertSameMessageModuloConsoleCodepage(expected, actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    // grimora.cs's default message uses "…" (U+2026). GrimoraCliRunner runs `dotnet grimora.dll` directly (the
    // same way a real user does), so on a box whose OEM codepage cannot represent "…" (proven here:
    // codepage 850, common on a Dutch/German Windows install), Console.WriteLine's best-fit fallback
    // silently rewrites it to "." before it ever reaches the pipe — a pre-existing grimora.cs/console
    // encoding characteristic, unrelated to this move and out of this slice's scope to fix (grimora.cs
    // stays unchanged, RESTRUCTURE.md section 0 rule 3). The new tool returns the un-mangled string
    // directly (no Console round-trip), so on such a box the two literal bytes differ only at the
    // ellipsis; this normalises both sides to the ASCII stand-in before comparing so the test still
    // proves real parity of everything Console.WriteLine did not silently rewrite.
    private static void AssertSameMessageModuloConsoleCodepage(string expected, string actual)
    {
        static string Normalize(string s) => s.Replace('…', '.');
        Assert.Equal(Normalize(expected), Normalize(actual));
    }
}
