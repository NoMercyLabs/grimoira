using Grimoira.TestSupport;
using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Facts.Tests;

public class ResolveToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndResolvesTheFinding()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("resolve-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("resolve-new");
        try
        {
            // Oracle: today's grimoira.cs ResolveFinding() (grimoira.cs:662).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            GrimoiraCliRunner.Run($"finding --instance {oldInstance} --title resolve-fixture");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"resolve --instance {oldInstance} 1");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ResolveTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            GrimoiraCliRunner.Run($"finding --instance {newInstance} --title resolve-fixture");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
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
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoOpenFindingForAnUnknownId()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("resolve-missing");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"resolve --instance {instance} 999");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ResolveTool().Execute(connection, 999).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no open finding #999.", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
