using Aitm.Store.Data;
using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

// RESTRUCTURE.md slice 31b, section 3.2 rules 3 and 7: user_version stays 3 while an old binary can
// still open the store; it only ever moves to 4 behind an explicit gate, not by default.
public class UserVersion4SchemaTests
{
    [Fact]
    public void GateOffProducesNoStatementsAndLeavesUserVersionAtThree()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"aitm-uv4-off-{Guid.NewGuid():N}.db");
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-uv4-off-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new StoreSchema()]);
            using (SqliteCommand pragma = connection.CreateCommand()) { pragma.CommandText = "PRAGMA user_version=3"; pragma.ExecuteNonQuery(); }

            UserVersion4Schema gateOff = new(gateValue: null);
            Assert.Empty(gateOff.Statements);

            SchemaRunResult result = SchemaRunner.Run(connection, [gateOff], backupDir);
            Assert.True(result.Success, result.Error);
            Assert.Equal(3L, UserVersion(connection));
        }
        finally { Cleanup(dbPath, backupDir); }
    }

    [Fact]
    public void GateOnMovesUserVersionToFour()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"aitm-uv4-on-{Guid.NewGuid():N}.db");
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-uv4-on-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new StoreSchema()]);
            using (SqliteCommand pragma = connection.CreateCommand()) { pragma.CommandText = "PRAGMA user_version=3"; pragma.ExecuteNonQuery(); }

            UserVersion4Schema gateOn = new(gateValue: "1");
            Assert.Single(gateOn.Statements);

            SchemaRunResult result = SchemaRunner.Run(connection, [gateOn], backupDir);
            Assert.True(result.Success, result.Error);
            Assert.Equal(4L, UserVersion(connection));
        }
        finally { Cleanup(dbPath, backupDir); }
    }

    [Fact]
    public void NoOwnEnvironmentVariableIsSetInThisProcessSoTheDefaultConstructorIsAlwaysOff()
    {
        // Nothing in the repo sets AITM_PHASE3_COMPLETE; the default (no-arg) constructor reads it from
        // the real environment, so it stays off unless a future slice's own process sets it.
        Assert.Null(Environment.GetEnvironmentVariable(UserVersion4Schema.Gate));
        Assert.Empty(new UserVersion4Schema().Statements);
    }

    private static long UserVersion(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static void Cleanup(string dbPath, string backupDir)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" }) { try { File.Delete(dbPath + suffix); } catch (IOException) { } }
        if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
    }
}
