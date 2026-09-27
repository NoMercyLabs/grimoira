using Grimora.Brain.Schema;
using Grimora.Docs.Schema;
using Grimora.Facts.Schema;
using Grimora.Facts.Tools;
using Grimora.Graph.Schema;
using Grimora.Memory.Schema;
using Grimora.Store.Data;
using Grimora.Store.Schema;
using Microsoft.Data.Sqlite;

namespace Grimora.Store.Tests.Support;

/// <summary>
/// A real-shaped v3 store built from the schema providers themselves: every provider's steps up to the
/// version-3 schema (no step markers, user_version 3, as the old build left it), then two facts added through <see cref="AddTool"/>. It replaces the store the old
/// grimora.cs build used to create (that build is deleted); the file lives under the temp folder, so no
/// real <c>~/.grimora</c> instance is ever touched. <see cref="Dispose"/> removes the file.
/// </summary>
internal sealed class V3StoreFixture : IDisposable
{
    public string DbPath { get; }

    private V3StoreFixture(string dbPath) => DbPath = dbPath;

    public static V3StoreFixture Create()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"grimora-store-tests-v3-{Guid.NewGuid():N}.db");
        using (SqliteConnection connection = StoreConnection.Open(dbPath))
        {
            SchemaRunner.Apply(
                connection,
                [new StoreSchema(), new FactsSchema(), new MemorySchema(), new DocsSchema(), new GraphSchema(), new BrainSchema()]);
            using (SqliteCommand version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version = 3";
                version.ExecuteNonQuery();
            }
            AddTool add = new();
            add.Execute(connection, "fixture-fact-one", "[]", "manual", "one", "", "", "stated");
            add.Execute(connection, "fixture-fact-two", "[]", "manual", "two", "", "", "stated");
        }
        SqliteConnection.ClearAllPools();
        return new V3StoreFixture(dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(DbPath + suffix); } catch (IOException) { }
        }
    }
}
