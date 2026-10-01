using Grimoira.TestSupport;
using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Facts.Tests;

public class TodosToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputForOpenTodos()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("todos-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("todos-new");
        try
        {
            // Oracle: today's grimoira.cs ListRows() (grimoira.cs:592) called for the `todos` verb (grimoira.cs:213).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            GrimoiraCliRunner.Run($"todo --instance {oldInstance} --title todos-fixture-one");
            GrimoiraCliRunner.Run($"todo --instance {oldInstance} --title todos-fixture-two");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"todos --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: TodosTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            GrimoiraCliRunner.Run($"todo --instance {newInstance} --title todos-fixture-one");
            GrimoiraCliRunner.Run($"todo --instance {newInstance} --title todos-fixture-two");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
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
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsZeroOpenTodosOnAFreshStore()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("todos-empty");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"todos --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new TodosTool().Execute(connection));

            Assert.Equal(expected, actual);
            Assert.Equal("(0 open todo(s))", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
