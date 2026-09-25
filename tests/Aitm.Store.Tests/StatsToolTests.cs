using Aitm.Store.Data;
using Aitm.TestSupport;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

public class StatsToolTests
{
    [Fact]
    public void MatchesTodaysCliOutput()
    {
        string instance = AitmCliRunner.NewTestInstance("stats");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"add --instance {instance} --term fixture-one --value one --category manual");
            AitmCliRunner.Run($"add --instance {instance} --term fixture-two --value two --category manual");
            AitmCliRunner.Run($"todo --instance {instance} --title open-item");

            (string stdout, int exitCode) = AitmCliRunner.Run($"stats --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new StatsTool().Execute(connection, instance, dbPath));

            Assert.Equal(expected, actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
