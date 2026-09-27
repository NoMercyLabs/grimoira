using Grimora.Store.Data;
using Grimora.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Store.Tests;

// Copied verbatim from grimora.cs's LogGap (grimora.cs:409-416).
public class GapLogTests
{
    [Fact]
    public void FirstMissInsertsAnOpenGapWithMissesOne()
    {
        using SqliteConnection connection = OpenInMemoryStore();
        GapLog.Record(connection, "query", "widget frobnicator");

        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT misses, status, tool FROM gaps WHERE query='widget frobnicator'";
        using SqliteDataReader reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("open", reader.GetString(1));
        Assert.Equal("query", reader.GetString(2));
    }

    [Fact]
    public void ARepeatedMissIncrementsMissesOnTheSameRow()
    {
        using SqliteConnection connection = OpenInMemoryStore();
        GapLog.Record(connection, "query", "widget frobnicator");
        GapLog.Record(connection, "query", "widget frobnicator");
        GapLog.Record(connection, "query", "widget frobnicator");

        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT count(*), max(misses) FROM gaps WHERE query='widget frobnicator'";
        using SqliteDataReader reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0)); // one row, not three
        Assert.Equal(3L, reader.GetInt64(1));
    }

    [Fact]
    public void AQueryUnderThreeCharactersIsNeverLogged()
    {
        using SqliteConnection connection = OpenInMemoryStore();
        GapLog.Record(connection, "query", "ab");

        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT count(*) FROM gaps";
        Assert.Equal(0L, (long)(select.ExecuteScalar() ?? 0L));
    }

    private static SqliteConnection OpenInMemoryStore()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        SchemaRunner.Apply(connection, [new StoreSchema()]);
        return connection;
    }
}
