using Grimora.Docs.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Docs.Tests;

public class RecompactDocsToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndRecompactsStoredContent()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("recompact-docs-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("recompact-docs-new");
        try
        {
            // Oracle: today's grimora.cs RecompactDocs() (grimora.cs:955).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            InsertUncompactedRow(GrimoraCliRunner.InstanceDbPath(oldInstance));
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"recompact-docs --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: RecompactDocsTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ReportsZeroChangedWhenNothingToRecompact()
    {
        string instance = GrimoraCliRunner.NewTestInstance("recompact-docs-none");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"recompact-docs --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new RecompactDocsTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("recompacted 0/0 section(s)", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
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
