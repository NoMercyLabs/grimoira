using Aitm.Facts.Tests.Support;
using Aitm.Facts.Tools;
using Aitm.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Facts.Tests;

public class ShedFactToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndDeletesTheRow()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("shed-fact-old");
        string newInstance = AitmCliRunner.NewTestInstance("shed-fact-new");
        try
        {
            // Oracle: today's aitm.cs ShedFact() (aitm.cs:1191).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"add --instance {oldInstance} --term shed-fixture --value one --category manual");
            (string stdout, int exitCode) = AitmCliRunner.Run($"shed-fact --instance {oldInstance} --key shed-fixture");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ShedFactTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            AitmCliRunner.Run($"add --instance {newInstance} --term shed-fixture --value one --category manual");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
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
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoFactForAnUnknownKey()
    {
        string instance = AitmCliRunner.NewTestInstance("shed-fact-missing");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"shed-fact --instance {instance} --key never-existed");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedFactTool().Execute(connection, "never-existed").Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no fact 'never-existed'.", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
