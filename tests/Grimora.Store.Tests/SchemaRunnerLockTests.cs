using Grimora.Store.Data;
using Grimora.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Store.Tests;

// RESTRUCTURE.md slice 3c: "two writers on one store wait instead of failing" — the busy_timeout pragma
// StoreConnection.Open applies (grimora.cs:27-35) must make a second writer block until the first commits,
// rather than throw SQLITE_BUSY.
public class SchemaRunnerLockTests
{
    [Fact]
    public async Task TwoWritersOnOneStoreWaitInsteadOfFailing()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"grimora-store-tests-lock-{Guid.NewGuid():N}.db");
        string backupDir = Path.Combine(Path.GetTempPath(), $"grimora-store-tests-backups-{Guid.NewGuid():N}");
        try
        {
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                SchemaRunResult result = SchemaRunner.Run(setup, [new StoreSchema()], backupDir);
                Assert.True(result.Success, result.Error);
            }
            SqliteConnection.ClearAllPools();

            using SqliteConnection writerA = StoreConnection.Open(dbPath);
            using SqliteTransaction transactionA = writerA.BeginTransaction();
            ExecIn(writerA, transactionA, "INSERT INTO meta(key, value) VALUES ('lock-test-a', 'A')");

            Task writerB = Task.Run(() =>
            {
                using SqliteConnection connectionB = StoreConnection.Open(dbPath);
                Exec(connectionB, "INSERT INTO meta(key, value) VALUES ('lock-test-b', 'B')");
            });

            // Give writer B a real chance to hit the lock and start waiting on busy_timeout before A
            // releases it — a version without busy_timeout would already have thrown by now.
            await Task.Delay(300);
            Assert.False(writerB.IsCompleted, "writer B should still be waiting on the held write lock");

            transactionA.Commit();

            Task finished = await Task.WhenAny(writerB, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(finished, writerB), "writer B should complete once the lock is released, well inside busy_timeout");
            await writerB;

            using SqliteConnection reader = StoreConnection.Open(dbPath);
            Assert.Equal("A", Scalar(reader, "SELECT value FROM meta WHERE key='lock-test-a'"));
            Assert.Equal("B", Scalar(reader, "SELECT value FROM meta WHERE key='lock-test-b'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(dbPath + suffix); } catch (IOException) { }
            }
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecIn(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar());
    }
}
