using Aitm.TestSupport;
using Aitm.Facts.Tools;
using Aitm.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Facts.Tests;

public class TodosToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputForOpenTodos()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("todos-old");
        string newInstance = AitmCliRunner.NewTestInstance("todos-new");
        try
        {
            // Oracle: today's aitm.cs ListRows() (aitm.cs:592) called for the `todos` verb (aitm.cs:213).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"todo --instance {oldInstance} --title todos-fixture-one");
            AitmCliRunner.Run($"todo --instance {oldInstance} --title todos-fixture-two");
            (string stdout, int exitCode) = AitmCliRunner.Run($"todos --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: TodosTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            AitmCliRunner.Run($"todo --instance {newInstance} --title todos-fixture-one");
            AitmCliRunner.Run($"todo --instance {newInstance} --title todos-fixture-two");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = Normalize(new TodosTool().Execute(connection));
            }

            Assert.Equal(expected, actual);
            Assert.Contains("(2 open todo(s))", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsZeroOpenTodosOnAFreshStore()
    {
        string instance = AitmCliRunner.NewTestInstance("todos-empty");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"todos --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new TodosTool().Execute(connection));

            Assert.Equal(expected, actual);
            Assert.Equal("(0 open todo(s))", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
