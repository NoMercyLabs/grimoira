using Grimoira.TestSupport;
using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Facts.Tests;

public class ShedFactToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndDeletesTheRow()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("shed-fact-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("shed-fact-new");
        try
        {
            // Oracle: today's grimoira.cs ShedFact() (grimoira.cs:1191).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            GrimoiraCliRunner.Run($"add --instance {oldInstance} --term shed-fixture --value one --category manual");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"shed-fact --instance {oldInstance} --key shed-fixture");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ShedFactTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            GrimoiraCliRunner.Run($"add --instance {newInstance} --term shed-fixture --value one --category manual");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new ShedFactTool().Execute(connection, "shed-fixture").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("shed fact 'shed-fixture'.", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM facts WHERE k='shed-fixture'";
            Assert.Equal(0L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoFactForAnUnknownKey()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("shed-fact-missing");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"shed-fact --instance {instance} --key never-existed");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedFactTool().Execute(connection, "never-existed").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no fact 'never-existed'.", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
