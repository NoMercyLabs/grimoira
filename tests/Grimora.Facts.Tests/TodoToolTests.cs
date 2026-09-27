using Grimora.TestSupport;
using Grimora.Facts.Tools;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Facts.Tests;

public class TodoToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndInsertsAnOpenTodo()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("todo-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("todo-new");
        try
        {
            // Oracle: today's grimora.cs AddTodo() (grimora.cs:639).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"todo --instance {oldInstance} --title todo-fixture --why because");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: TodoTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }
}
