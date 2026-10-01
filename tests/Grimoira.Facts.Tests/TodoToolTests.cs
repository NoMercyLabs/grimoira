using Grimoira.TestSupport;
using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Facts.Tests;

public class TodoToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndInsertsAnOpenTodo()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("todo-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("todo-new");
        try
        {
            // Oracle: today's grimoira.cs AddTodo() (grimoira.cs:639).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"todo --instance {oldInstance} --title todo-fixture --why because");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: TodoTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new TodoTool().Execute(connection, "todo-fixture", "because").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("todo added.", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand select = check.CreateCommand();
            select.CommandText = "SELECT title, status, why FROM todos";
            using SqliteDataReader reader = select.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("todo-fixture", reader.GetString(0));
            Assert.Equal("open", reader.GetString(1));
            Assert.Equal("because", reader.GetString(2));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }
}
