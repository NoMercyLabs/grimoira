using Grimora.TestSupport;
using Grimora.Facts.Tools;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Facts.Tests;

public class TodosToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputForOpenTodos()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("todos-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("todos-new");
        try
        {
            // Oracle: today's grimora.cs ListRows() (grimora.cs:592) called for the `todos` verb (grimora.cs:213).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            GrimoraCliRunner.Run($"todo --instance {oldInstance} --title todos-fixture-one");
            GrimoraCliRunner.Run($"todo --instance {oldInstance} --title todos-fixture-two");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"todos --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: TodosTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            GrimoraCliRunner.Run($"todo --instance {newInstance} --title todos-fixture-one");
            GrimoraCliRunner.Run($"todo --instance {newInstance} --title todos-fixture-two");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void CliShapeReportsZeroOpenTodosOnAFreshStore()
    {
        string instance = GrimoraCliRunner.NewTestInstance("todos-empty");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"todos --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new TodosTool().Execute(connection));

            Assert.Equal(expected, actual);
            Assert.Equal("(0 open todo(s))", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
