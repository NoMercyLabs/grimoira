using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Facts.Tests;

// RESTRUCTURE.md slice 24 bullet 1: DoneTool had no pinned test of its own (slice 6's card excused it:
// "all of them except done (selftest has it)"). Ports the 2 `selftest` checks that covered it (grimoira.cs's
// old SelfTest: "todos: open and done both logged" / "guard: closing a nonexistent todo is a no-op (no
// phantom mutation)") now that selftest itself is gone.
public class DoneToolTests
{
    [Fact]
    public void ClosingAnOpenTodoLogsTheUpdateAlongsideTheOriginalInsert()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("done-tool-open-and-close");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            (string _, int exitCode) = GrimoiraCliRunner.Run($"todo --instance {instance} --title t1");
            Assert.Equal(0, exitCode);

            int id;
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                using SqliteCommand select = setup.CreateCommand();
                select.CommandText = "SELECT id FROM todos WHERE title='t1'";
                id = (int)(long)select.ExecuteScalar()!;
            }

            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string result = new DoneTool().Execute(connection, id);

            Assert.Equal($"todo #{id} closed.", result);
            using SqliteCommand mutationCount = connection.CreateCommand();
            mutationCount.CommandText = "SELECT count(*) FROM mutations WHERE kind='todo'";
            Assert.Equal(2L, (long)mutationCount.ExecuteScalar()!); // one for the insert, one for this close
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void ClosingANonexistentTodoIsANoOpAndLogsNoPhantomMutation()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("done-tool-noop");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            using (SqliteCommand before = connection.CreateCommand())
            {
                before.CommandText = "SELECT count(*) FROM mutations WHERE kind='todo'";
                Assert.Equal(0L, (long)before.ExecuteScalar()!);
            }

            string result = new DoneTool().Execute(connection, 99999);

            Assert.Equal("no open todo #99999.", result);
            using SqliteCommand after = connection.CreateCommand();
            after.CommandText = "SELECT count(*) FROM mutations WHERE kind='todo'";
            Assert.Equal(0L, (long)after.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
