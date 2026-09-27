using Grimora.TestSupport;
using Grimora.Facts.Tools;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Facts.Tests;

public class ShedFactToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndDeletesTheRow()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("shed-fact-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("shed-fact-new");
        try
        {
            // Oracle: today's grimora.cs ShedFact() (grimora.cs:1191).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            GrimoraCliRunner.Run($"add --instance {oldInstance} --term shed-fixture --value one --category manual");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"shed-fact --instance {oldInstance} --key shed-fixture");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ShedFactTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            GrimoraCliRunner.Run($"add --instance {newInstance} --term shed-fixture --value one --category manual");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoFactForAnUnknownKey()
    {
        string instance = GrimoraCliRunner.NewTestInstance("shed-fact-missing");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"shed-fact --instance {instance} --key never-existed");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedFactTool().Execute(connection, "never-existed").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no fact 'never-existed'.", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }
}
