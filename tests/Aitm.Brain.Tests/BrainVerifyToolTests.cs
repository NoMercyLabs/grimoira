using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

// RESTRUCTURE.md slice 24 bullet 1: BrainVerifyTool had no pinned test of its own (slice 17a's card
// excused it: "selftest has verify"). Ports the 1 `selftest` check that covered it (aitm.cs's old
// SelfTest: "verify: confirmation timestamp recorded") now that selftest itself is gone.
public class BrainVerifyToolTests
{
    [Fact]
    public void VerifyingALiveNodeRecordsAConfirmationTimestamp()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-verify-tool");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            BrainTestFixtures.InsertNode(dbPath, "c:s", "concept", "CS", "");

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new BrainVerifyTool().Execute(connection, "c:s");

            Assert.Equal("verified c:s (confirmed against current code).", result);
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM usage WHERE node_k='c:s' AND verified_at IS NOT NULL";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void VerifyingAMissingNodeReportsNoLiveNodeAndWritesNothing()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-verify-tool-missing");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            string result = new BrainVerifyTool().Execute(connection, "no:such-node");

            Assert.Equal("no live node 'no:such-node'.", result);
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM usage WHERE node_k='no:such-node'";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
