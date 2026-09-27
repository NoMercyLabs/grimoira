using Aitm.Brain.Schema;
using Aitm.Docs.Schema;
using Aitm.Facts.Schema;
using Aitm.Facts.Tools;
using Aitm.Graph.Schema;
using Aitm.Memory.Schema;
using Aitm.Store.Data;
using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;

namespace Aitm.Store.Tests.Support;

/// <summary>
/// A real-shaped v3 store built from the schema providers themselves: every provider's steps up to the
/// version-3 schema (no step markers, user_version 3, as the old build left it), then two facts added through <see cref="AddTool"/>. It replaces the store the old
/// aitm.cs build used to create (that build is deleted); the file lives under the temp folder, so no
/// real <c>~/.aitm</c> instance is ever touched. <see cref="Dispose"/> removes the file.
/// </summary>
internal sealed class V3StoreFixture : IDisposable
{
    public string DbPath { get; }

    private V3StoreFixture(string dbPath) => DbPath = dbPath;

    public static V3StoreFixture Create()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-v3-{Guid.NewGuid():N}.db");
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
