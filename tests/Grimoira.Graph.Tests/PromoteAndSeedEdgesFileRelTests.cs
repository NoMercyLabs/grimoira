using Grimoira.Graph.Schema;
using Grimoira.Store.Data;
using Grimoira.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Graph.Tests;

// RESTRUCTURE.md slice 31b: PromoteTool and SeedEdgesTool write edges.file_rel with the same
// column-exists guard GraphFileRelSchema already uses (GraphFileRelSchema.HasColumn), so a v3 store
// (no file_rel column) never errors, and a migrated store (file_rel column present) gets it filled.
// Every fixture here is a temp file under Path.GetTempPath() — never ~/.grimoira and never the live store.
public class PromoteAndSeedEdgesFileRelTests
{
    [Fact]
    public void PromoteOnAV3FixtureInsertsTheSameRowWithNoError()
    {
        (string dbPath, string root) = NewFixture();
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new Grimoira.Store.Schema.StoreSchema(), new GraphSchema()]);
            RegisterProject(connection, "web", root);
            int id = InsertCandidate(connection, "Widget", "web", "widget.ts", 1);

            string message = new Grimoira.Graph.Tools.PromoteTool().Execute(connection, id);

            Assert.Contains("promoted", message);
            Assert.False(GraphFileRelSchema.HasColumn(connection, "edges", "file_rel"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT count(*) FROM edges WHERE symbol='Widget'"));
        }
        finally { Cleanup(dbPath, root); }
    }

    [Fact]
    public void PromoteOnAMigratedStoreFillsFileRel()
    {
        (string dbPath, string root) = NewFixture();
        string backupDir = Path.Combine(Path.GetTempPath(), $"grimoira-31b-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new Grimoira.Store.Schema.StoreSchema(), new GraphSchema()]);
            RegisterProject(connection, "web", root);
            Assert.True(SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir).Success);
            int id = InsertCandidate(connection, "Widget", "web", "widget.ts", 1);

            new Grimoira.Graph.Tools.PromoteTool().Execute(connection, id);

            Assert.Equal("widget.ts", ScalarString(connection, "SELECT file_rel FROM edges WHERE symbol='Widget'"));
        }
        finally { Cleanup(dbPath, root); Directory.Delete(backupDir, recursive: true); }
    }

    [Fact]
    public void SeedEdgesOnAV3FixtureInsertsWithNoError()
    {
        (string dbPath, string root) = NewFixture();
        string spinePath = Path.Combine(Path.GetTempPath(), $"grimoira-31b-spine-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(spinePath, """
                { "edges": [ { "symbol":"Gadget","contract":"IGadget","project":"web","file":"gadget.ts","line":3,"usage":"call","hardcoded":0 } ] }
                """);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new Grimoira.Store.Schema.StoreSchema(), new GraphSchema()]);
            RegisterProject(connection, "web", root);

            string message = new Grimoira.Graph.Tools.SeedEdgesTool().Execute(connection, spinePath);

            Assert.Contains("seeded 1", message);
            Assert.False(GraphFileRelSchema.HasColumn(connection, "edges", "file_rel"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT count(*) FROM edges WHERE symbol='Gadget'"));
        }
        finally { Cleanup(dbPath, root); if (File.Exists(spinePath)) File.Delete(spinePath); }
    }

    [Fact]
    public void SeedEdgesOnAMigratedStoreFillsFileRel()
    {
        (string dbPath, string root) = NewFixture();
        string backupDir = Path.Combine(Path.GetTempPath(), $"grimoira-31b-backups-{Guid.NewGuid():N}");
        string spinePath = Path.Combine(Path.GetTempPath(), $"grimoira-31b-spine-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(spinePath, """
                { "edges": [ { "symbol":"Gadget","contract":"IGadget","project":"web","file":"gadget.ts","line":3,"usage":"call","hardcoded":0 } ] }
                """);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new Grimoira.Store.Schema.StoreSchema(), new GraphSchema()]);
            RegisterProject(connection, "web", root);
            Assert.True(SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir).Success);

            new Grimoira.Graph.Tools.SeedEdgesTool().Execute(connection, spinePath);

            Assert.Equal("gadget.ts", ScalarString(connection, "SELECT file_rel FROM edges WHERE symbol='Gadget'"));
        }
        finally { Cleanup(dbPath, root); Directory.Delete(backupDir, recursive: true); if (File.Exists(spinePath)) File.Delete(spinePath); }
    }

    // ---- fixture plumbing -------------------------------------------------------------------

    private static (string dbPath, string root) NewFixture()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"grimoira-31b-{Guid.NewGuid():N}.db");
        string root = Path.Combine(Path.GetTempPath(), $"grimoira-31b-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return (dbPath, root);
    }

    private static void Cleanup(string dbPath, string root)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" }) { try { File.Delete(dbPath + suffix); } catch (IOException) { } }
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static void RegisterProject(SqliteConnection connection, string name, string root)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO projects(name, root, lang, globs) VALUES($n,$r,'ts','*.ts')";
        insert.Parameters.AddWithValue("$n", name);
        insert.Parameters.AddWithValue("$r", root);
        insert.ExecuteNonQuery();
    }

    private static int InsertCandidate(SqliteConnection connection, string symbol, string project, string file, int line)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO edge_candidates(symbol,contract,project,file,line,usage,hardcoded,status) " +
            "VALUES($s,'decl',$p,$f,$l,'ts declaration',0,'pending'); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$s", symbol);
        insert.Parameters.AddWithValue("$p", project);
        insert.Parameters.AddWithValue("$f", file);
        insert.Parameters.AddWithValue("$l", line);
        return (int)(long)(insert.ExecuteScalar() ?? 0L);
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        object? v = cmd.ExecuteScalar();
        return v is DBNull or null ? "<null>" : Convert.ToString(v)!;
    }
}
