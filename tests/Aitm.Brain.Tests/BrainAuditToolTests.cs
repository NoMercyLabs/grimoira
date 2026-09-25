using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainAuditToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputForAnOrphanNode()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-audit-cli-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-audit-cli-new");
        try
        {
            // Oracle: today's aitm.cs BrainAudit() (aitm.cs:1858). aitm.cs prints the live `instance`
            // global as the header; the new tool takes it as a parameter (StatsTool/InitTool's pattern),
            // so both sides are run against the same instance name for a byte-identical header.
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            BrainTestFixtures.InsertNode(oldDb, "concept:orphan", "concept", "Orphan", "no edges, no slots, no refs");
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain audit --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            BrainTestFixtures.InsertNode(newDb, "concept:orphan", "concept", "Orphan", "no edges, no slots, no refs");

            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainAuditTool().Execute(connection, oldInstance).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("concept:orphan", actual);
            Assert.Contains("orphan nodes", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }
}
