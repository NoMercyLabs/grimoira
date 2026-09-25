using Aitm.Store.Data;
using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

// Copied verbatim from mcp.cs's ReinforceChannel (mcp.cs:190-202).
public class UsageSignalTests
{
    [Fact]
    public void ReinforcingAPayloadKeyBumpsHitsForItsLinkedNode()
    {
        using SqliteConnection connection = OpenInMemoryStore();
        Exec(connection, "INSERT INTO ref(node_k,channel,payload_k) VALUES('node:a','facts','fact-key')");

        new UsageSignal().Reinforce(connection, "facts", ["fact-key"]);
        new UsageSignal().Reinforce(connection, "facts", ["fact-key"]);

        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT hits FROM usage WHERE node_k='node:a'";
        Assert.Equal(2L, (long)(select.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void AnUnlinkedPayloadKeyNeverCreatesAUsageRow()
    {
        using SqliteConnection connection = OpenInMemoryStore();
        new UsageSignal().Reinforce(connection, "facts", ["no-such-payload"]);

        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT count(*) FROM usage";
        Assert.Equal(0L, (long)(select.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void AMissingRefTableIsSwallowedRatherThanThrown()
    {
        // A minimal store — before InitBrain-style tables exist — has no `ref` table at all. Today's
        // ReinforceChannel swallows that SqliteException; the port must too.
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        SchemaRunner.Apply(connection, [new StoreSchema()]);

        Exception? thrown = Record.Exception(() => new UsageSignal().Reinforce(connection, "facts", ["anything"]));
        Assert.Null(thrown);
    }

    private static SqliteConnection OpenInMemoryStore()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        SchemaRunner.Apply(connection, [new StoreSchema()]);
        Exec(connection, "CREATE TABLE ref(node_k TEXT, channel TEXT, payload_k TEXT)");
        return connection;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
