using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainAuditToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAnOrphanNode()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-audit-cli-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-audit-cli-new");
        try
        {
            // Oracle: today's grimora.cs BrainAudit() (grimora.cs:1858). grimora.cs prints the live `instance`
            // global as the header; the new tool takes it as a parameter (StatsTool/InitTool's pattern),
            // so both sides are run against the same instance name for a byte-identical header.
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "concept:orphan", "concept", "Orphan", "no edges, no slots, no refs");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain audit --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "concept:orphan", "concept", "Orphan", "no edges, no slots, no refs");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainAuditTool().Execute(connection, oldInstance).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("concept:orphan", actual);
            Assert.Contains("orphan nodes", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }
}
