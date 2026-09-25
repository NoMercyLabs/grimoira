using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainToolTests
{
    [Fact]
    public void RouterDispatchesCoreToTheSameOutputAsTheStandaloneTool()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-router-core");
        try
        {
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"init --instance {instance}");
            BrainTestFixtures.InsertHardNode(dbPath, "rule:router", "rule", "Routed", "via brain core");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string viaRouter = new BrainTool().ExecuteCli(connection, ["core"]);
            string viaTool = new BrainCoreTool().ExecuteCli(connection);

            Assert.Equal(viaTool, viaRouter);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void UnknownSubVerbMatchesTodaysCliExitCodeAndMessage()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-router-unknown");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            // Oracle: today's aitm.cs BrainCmd default case (aitm.cs:1354-1357).
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain --instance {instance} not-a-real-subverb");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainTool().ExecuteCli(connection, ["not-a-real-subverb"]).Trim();

            AssertSameMessageModuloConsoleCodepage(expected, actual);
            Assert.Equal(
                "brain <core|scope <proj…>|common <proj…>|place <codekind>|recall <text>|impact <symbol>|learn …|gaps|distill|stats>",
                new BrainTool().Help);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void NoSubVerbMatchesTodaysCliExitCodeAndMessage()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-router-empty");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            // "brain" with no sub-verb defaults to sub == "help" in aitm.cs's BrainCmd (aitm.cs:1305),
            // which also falls into the default case.
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new BrainTool().ExecuteCli(connection, []).Trim();

            AssertSameMessageModuloConsoleCodepage(expected, actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    // aitm.cs's default message uses "…" (U+2026). AitmCliRunner runs `dotnet aitm.dll` directly (the
    // same way a real user does), so on a box whose OEM codepage cannot represent "…" (proven here:
    // codepage 850, common on a Dutch/German Windows install), Console.WriteLine's best-fit fallback
    // silently rewrites it to "." before it ever reaches the pipe — a pre-existing aitm.cs/console
    // encoding characteristic, unrelated to this move and out of this slice's scope to fix (aitm.cs
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
