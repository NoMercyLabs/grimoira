using Aitm.Docs.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Docs.Tests;

public class RecompactDocsToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndRecompactsStoredContent()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("recompact-docs-old");
        string newInstance = AitmCliRunner.NewTestInstance("recompact-docs-new");
        try
        {
            // Oracle: today's aitm.cs RecompactDocs() (aitm.cs:955).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            InsertUncompactedRow(AitmCliRunner.InstanceDbPath(oldInstance));
            (string stdout, int exitCode) = AitmCliRunner.Run($"recompact-docs --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: RecompactDocsTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
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
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ReportsZeroChangedWhenNothingToRecompact()
    {
        string instance = AitmCliRunner.NewTestInstance("recompact-docs-none");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);

            (string stdout, int exitCode) = AitmCliRunner.Run($"recompact-docs --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new RecompactDocsTool().Execute(connection).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("recompacted 0/0 section(s)", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
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
