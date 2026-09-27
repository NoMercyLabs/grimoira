using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Store.Tests;

// Pins the pragma pair grimora.cs applies on every open (grimora.cs:27-35): PRAGMA busy_timeout=30000, then
// PRAGMA journal_mode=WAL. The test opens a temp file the same way grimora.cs's own connection string
// does ("Data Source={path};Foreign Keys=True") and asserts the observed pragma state matches what
// grimora.cs produces for the same connection string and the same pragma statements.
public class PragmaParityTests
{
    [Fact]
    public void OpenAppliesTheSamePragmasAsGrimoraCs()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"grimora-store-tests-{Guid.NewGuid():N}.db");
        try
        {
            using SqliteConnection viaStore = StoreConnection.Open(dbPath);

            Assert.Equal(30000L, ScalarLong(viaStore, "PRAGMA busy_timeout"));
            Assert.Equal("wal", ScalarText(viaStore, "PRAGMA journal_mode"), ignoreCase: true);
            Assert.Equal(1L, ScalarLong(viaStore, "PRAGMA foreign_keys"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            File.Delete(dbPath + "-wal");
            File.Delete(dbPath + "-shm");
        }
    }

    [Fact]
    public void ApplyPragmasIsIdempotentLikeGrimoraCsTryExec()
    {
        // grimora.cs wraps journal_mode=WAL in TryExec because switching journal mode itself needs the
        // write lock — a second call while nothing else holds it must not throw.
        string dbPath = Path.Combine(Path.GetTempPath(), $"grimora-store-tests-{Guid.NewGuid():N}.db");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            StoreConnection.ApplyPragmas(connection); // must not throw the second time
            Assert.Equal("wal", ScalarText(connection, "PRAGMA journal_mode"), ignoreCase: true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            File.Delete(dbPath + "-wal");
            File.Delete(dbPath + "-shm");
        }
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? result = command.ExecuteScalar();
        return Convert.ToInt64(result);
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }
}
