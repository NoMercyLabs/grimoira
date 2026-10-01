using Grimoira.Docs.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Docs.Tests;

public class RecompactDocsToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndRecompactsStoredContent()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("recompact-docs-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("recompact-docs-new");
        try
        {
            // Oracle: today's grimoira.cs RecompactDocs() (grimoira.cs:955).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            InsertUncompactedRow(GrimoiraCliRunner.InstanceDbPath(oldInstance));
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"recompact-docs --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: RecompactDocsTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            InsertUncompactedRow(dbPath);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new RecompactDocsTool().Execute(connection).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Contains("recompacted 1/1 section(s)", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand read = check.CreateCommand();
            read.CommandText = "SELECT content FROM docs WHERE k='r:1'";
            Assert.Equal("Bold extra spaces", read.ExecuteScalar() as string);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ReportsZeroChangedWhenNothingToRecompact()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("recompact-docs-none");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"recompact-docs --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new RecompactDocsTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("recompacted 0/0 section(s)", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static void InsertUncompactedRow(string dbPath)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO docs(k,path,title,category,content,terms) VALUES('r:1','p','t','doc','**Bold**   extra   spaces','')";
            insert.ExecuteNonQuery();
        }
        using SqliteCommand ins = connection.CreateCommand();
        ins.CommandText = "INSERT INTO docs_fts(k,title,content) VALUES('r:1','t','**Bold**   extra   spaces')";
        ins.ExecuteNonQuery();
    }
}
