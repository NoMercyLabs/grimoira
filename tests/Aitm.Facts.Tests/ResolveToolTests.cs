using Aitm.Facts.Tests.Support;
using Aitm.Facts.Tools;
using Aitm.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Facts.Tests;

public class ResolveToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndResolvesTheFinding()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("resolve-old");
        string newInstance = AitmCliRunner.NewTestInstance("resolve-new");
        try
        {
            // Oracle: today's aitm.cs ResolveFinding() (aitm.cs:662).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"finding --instance {oldInstance} --title resolve-fixture");
            (string stdout, int exitCode) = AitmCliRunner.Run($"resolve --instance {oldInstance} 1");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ResolveTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            AitmCliRunner.Run($"finding --instance {newInstance} --title resolve-fixture");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new ResolveTool().Execute(connection, 1).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("finding #1 resolved.", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand select = check.CreateCommand();
            select.CommandText = "SELECT status FROM findings WHERE id=1";
            Assert.Equal("resolved", (string)(select.ExecuteScalar() ?? ""));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoOpenFindingForAnUnknownId()
    {
        string instance = AitmCliRunner.NewTestInstance("resolve-missing");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"resolve --instance {instance} 999");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ResolveTool().Execute(connection, 999).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no open finding #999.", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
