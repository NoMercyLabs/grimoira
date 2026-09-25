using Aitm.Brain.Schema;
using Aitm.Brain.Tools;
using Aitm.Graph.Schema;
using Aitm.Store.Data;
using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

// RESTRUCTURE.md slice 31b: SpineImportTool writes edges.file_rel with the same column-exists guard
// GraphFileRelSchema already uses; legacy_consumes is recreated (backup-first, through SchemaRunner) to
// expose project and file_rel so brain_impact can resolve a full path for THIS machine. Every fixture
// here is a temp file under Path.GetTempPath() — never ~/.aitm and never the live store.
public class BrainLegacyConsumesFileRelTests
{
    [Fact]
    public void SpineImportOnAV3FixtureInsertsTheEdgeWithNoError()
    {
        (string dbPath, string root) = NewFixture();
        string spinePath = Path.Combine(Path.GetTempPath(), $"aitm-31b-spine-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(spinePath, SpineWithOneEdge("web", "widget.ts"));
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new Aitm.Store.Schema.StoreSchema(), new GraphSchema(), new BrainSchema()]);
            RegisterProject(connection, "web", root);

            string message = new SpineImportTool().ExecuteCli(connection, spinePath);

            Assert.Contains("1 edge(s)", message);
            Assert.False(GraphFileRelSchema.HasColumn(connection, "edges", "file_rel"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT count(*) FROM edges WHERE symbol='Widget'"));
        }
        finally { Cleanup(dbPath, root); if (File.Exists(spinePath)) File.Delete(spinePath); }
    }

    [Fact]
    public void SpineImportOnAMigratedStoreFillsFileRel()
    {
        (string dbPath, string root) = NewFixture();
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-31b-backups-{Guid.NewGuid():N}");
        string spinePath = Path.Combine(Path.GetTempPath(), $"aitm-31b-spine-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(spinePath, SpineWithOneEdge("web", "widget.ts"));
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new Aitm.Store.Schema.StoreSchema(), new GraphSchema(), new BrainSchema()]);
            RegisterProject(connection, "web", root);
            Assert.True(SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir).Success);

            new SpineImportTool().ExecuteCli(connection, spinePath);

            Assert.Equal("widget.ts", ScalarString(connection, "SELECT file_rel FROM edges WHERE symbol='Widget'"));
        }
        finally { Cleanup(dbPath, root); Directory.Delete(backupDir, recursive: true); if (File.Exists(spinePath)) File.Delete(spinePath); }
    }

    [Fact]
    public void SchemaStepRecreatesTheViewOnceWithABackupAndASecondRunIsANoOp()
    {
        (string dbPath, string root) = NewFixture();
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-31b-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new Aitm.Store.Schema.StoreSchema(), new GraphSchema(), new BrainSchema()]);
            RegisterProject(connection, "web", root);
            InsertEdge(connection, "Widget", "IWidget", "web", "widget.ts", 1, "call", 0);
            Assert.True(SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir).Success);
            GraphFileRelSchema.Backfill(connection);

            Assert.False(GraphFileRelSchema.HasColumn(connection, "legacy_consumes", "file_rel"));
            long before = ScalarLong(connection, "SELECT count(*) FROM legacy_consumes");

            SchemaRunResult first = SchemaRunner.Run(connection, [new BrainLegacyConsumesFileRelSchema(connection)], backupDir);
            Assert.True(first.Success, first.Error);
            Assert.Contains("BrainLegacyConsumesFileRel", first.AppliedProviders);
            string[] backups = Directory.GetFiles(backupDir, "pre-BrainLegacyConsumesFileRel-*.db");
            Assert.Single(backups);

            Assert.True(GraphFileRelSchema.HasColumn(connection, "legacy_consumes", "file_rel"));
            Assert.Equal(before, ScalarLong(connection, "SELECT count(*) FROM legacy_consumes"));
            Assert.Equal("widget.ts", ScalarString(connection, "SELECT file_rel FROM legacy_consumes WHERE o LIKE '%Widget%'"));

            SchemaRunResult second = SchemaRunner.Run(connection, [new BrainLegacyConsumesFileRelSchema(connection)], backupDir);
            Assert.True(second.Success, second.Error);
            Assert.Empty(new BrainLegacyConsumesFileRelSchema(connection).Statements);
        }
        finally { Cleanup(dbPath, root); Directory.Delete(backupDir, recursive: true); }
    }

    [Fact]
    public void BrainImpactAnswersEqualAfterTheProjectRootMoves()
    {
        (string dbPath, string root) = NewFixture();
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-31b-backups-{Guid.NewGuid():N}");
        string movedRoot = Path.Combine(Path.GetTempPath(), $"aitm-31b-moved-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new Aitm.Store.Schema.StoreSchema(), new GraphSchema(), new BrainSchema()]);
            RegisterProject(connection, "web", root);
            InsertEdge(connection, "Widget", "IWidget", "web", "widget.ts", 1, "call", 0);
            Assert.True(SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir).Success);
            GraphFileRelSchema.Backfill(connection);
            Assert.True(SchemaRunner.Run(connection, [new BrainLegacyConsumesFileRelSchema(connection)], backupDir).Success);

            string beforeMove = new BrainImpactTool().ExecuteCli(connection, "Widget");

            using (SqliteCommand upd = connection.CreateCommand())
            {
                upd.CommandText = "UPDATE projects SET root=$r WHERE name='web'";
                upd.Parameters.AddWithValue("$r", movedRoot);
                upd.ExecuteNonQuery();
            }

            string afterMove = new BrainImpactTool().ExecuteCli(connection, "Widget");

            Assert.Contains("Widget", beforeMove);
            Assert.Contains("Widget", afterMove);
            Assert.Contains(root.Replace('\\', '/'), beforeMove);
            Assert.Contains(movedRoot.Replace('\\', '/'), afterMove);
            Assert.Equal(beforeMove.Split('|')[0], afterMove.Split('|')[0]);
        }
        finally { Cleanup(dbPath, root); Directory.Delete(backupDir, recursive: true); }
    }

    // ---- fixture plumbing -------------------------------------------------------------------

    private static (string dbPath, string root) NewFixture()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"aitm-31b-brain-{Guid.NewGuid():N}.db");
        string root = Path.Combine(Path.GetTempPath(), $"aitm-31b-brain-root-{Guid.NewGuid():N}");
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

    private static void InsertEdge(SqliteConnection connection, string symbol, string contract, string project, string file, int line, string usage, int hardcoded)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,$h)";
        insert.Parameters.AddWithValue("$s", symbol);
        insert.Parameters.AddWithValue("$c", contract);
        insert.Parameters.AddWithValue("$p", project);
        insert.Parameters.AddWithValue("$f", file);
        insert.Parameters.AddWithValue("$l", line);
        insert.Parameters.AddWithValue("$u", usage);
        insert.Parameters.AddWithValue("$h", hardcoded);
        insert.ExecuteNonQuery();
    }

    private static string SpineWithOneEdge(string project, string file) => $$"""
        {
          "nodes": [], "slots": [], "links": [], "aliases": [], "terms": [],
          "edges": [ { "symbol":"Widget","contract":"IWidget","project":"{{project}}","file":"{{file}}","line":1,"usage":"call","hardcoded":0 } ]
        }
        """;

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
