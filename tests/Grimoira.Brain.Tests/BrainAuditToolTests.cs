using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainAuditToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAnOrphanNode()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-audit-cli-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-audit-cli-new");
        try
        {
            // Oracle: today's grimoira.cs BrainAudit() (grimoira.cs:1858). grimoira.cs prints the live `instance`
            // global as the header; the new tool takes it as a parameter (StatsTool/InitTool's pattern),
            // so both sides are run against the same instance name for a byte-identical header.
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "concept:orphan", "concept", "Orphan", "no edges, no slots, no refs");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain audit --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "concept:orphan", "concept", "Orphan", "no edges, no slots, no refs");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainAuditTool().Execute(connection, oldInstance).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("concept:orphan", actual);
            Assert.Contains("orphan nodes", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }
}
