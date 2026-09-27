using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

// RESTRUCTURE.md slice 3a: "Store's own tables' DDL identical to today's (compare sqlite_master rows
// on an empty temp store)". TodaysStoreTableStatements is a verbatim copy of the 4 CREATE TABLE
// statements aitm.cs runs for the tables Store owns (aitm.cs:380 mutations, :405 gaps, :406 meta,
// :585 usage) — the oracle, not a reimplementation.
public class StoreSchemaDdlParityTests
{
    private static readonly string[] TodaysStoreTableStatements =
    [
        "CREATE TABLE IF NOT EXISTS mutations(id INTEGER PRIMARY KEY, ts TEXT, op TEXT, kind TEXT, k TEXT, before TEXT, after TEXT, why TEXT);",
        "CREATE TABLE IF NOT EXISTS gaps(id INTEGER PRIMARY KEY, query TEXT NOT NULL UNIQUE, tool TEXT NOT NULL, misses INTEGER NOT NULL DEFAULT 1, first_ts TEXT NOT NULL, last_ts TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'open');",
        "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);",
        "CREATE TABLE IF NOT EXISTS usage (node_k TEXT PRIMARY KEY, hits INTEGER NOT NULL DEFAULT 0, last_used TEXT, verified_at TEXT);",
    ];

    private static readonly string[] StoreOwnedTables = ["meta", "mutations", "gaps", "usage"];

    [Fact]
    public void StoreSchemaMatchesTodaysDdlForTheTablesStoreOwns()
    {
        using SqliteConnection oldStore = OpenMemory();
        foreach (string statement in TodaysStoreTableStatements) Exec(oldStore, statement);

        using SqliteConnection newStore = OpenMemory();
        SchemaRunner.Apply(newStore, [new StoreSchema()]);

        Dictionary<string, string> oldRows = SqliteMasterRows(oldStore, StoreOwnedTables);
        Dictionary<string, string> newRows = SqliteMasterRows(newStore, StoreOwnedTables);

        Assert.Equal(StoreOwnedTables.Length, oldRows.Count);
        Assert.Equal(oldRows, newRows);
    }

    private static SqliteConnection OpenMemory()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, string> SqliteMasterRows(SqliteConnection connection, IEnumerable<string> tables)
    {
        string[] tableNames = [.. tables];
        Dictionary<string, string> rows = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' AND name IN (" +
            string.Join(",", tableNames.Select((_, i) => $"$n{i}")) + ")";
        int idx = 0;
        foreach (string name in tableNames) command.Parameters.AddWithValue($"$n{idx++}", name);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }
}
