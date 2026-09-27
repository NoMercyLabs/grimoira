using Grimora.TestSupport;
using Grimora.Facts.Tools;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Facts.Tests;

public class ResolveToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndResolvesTheFinding()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("resolve-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("resolve-new");
        try
        {
            // Oracle: today's grimora.cs ResolveFinding() (grimora.cs:662).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            GrimoraCliRunner.Run($"finding --instance {oldInstance} --title resolve-fixture");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"resolve --instance {oldInstance} 1");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ResolveTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            GrimoraCliRunner.Run($"finding --instance {newInstance} --title resolve-fixture");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsNoOpenFindingForAnUnknownId()
    {
        string instance = GrimoraCliRunner.NewTestInstance("resolve-missing");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"resolve --instance {instance} 999");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ResolveTool().Execute(connection, 999).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("no open finding #999.", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }
}
