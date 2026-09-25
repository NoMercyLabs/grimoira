using Aitm.Graph.Schema;
using Aitm.Graph.Tools;
using Aitm.Store.Data;
using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Graph.Tests;

// RESTRUCTURE.md phase 3 slice 31 ("Graph paths become project-relative"): a schema step to a new
// edges.file_rel column, backup first, filled once for existing rows from each row's project root.
// Every fixture here is a temp file under Path.GetTempPath() — never ~/.aitm and never the live store.
public class GraphFileRelSchemaTests
{
    [Fact]
    public void MigrationKeepsEveryRowAndBacksUpFirst()
    {
        (string dbPath, string backupDir, string root) = NewFixture();
        try
        {
            SeedProjectAndEdges(dbPath, root, outsideRootRows: 0);

            long before = RowCount(dbPath);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                SchemaRunResult result = SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir);
                Assert.True(result.Success, result.Error);
                Assert.Contains("GraphFileRel", result.AppliedProviders);
            }

            Assert.Equal(before, RowCount(dbPath));
            Assert.True(Directory.Exists(backupDir));
            string[] backups = Directory.GetFiles(backupDir, "pre-GraphFileRel-*.db");
            Assert.Single(backups);
            AssertBackupIntegrityOk(backups[0]);
        }
        finally { Cleanup(dbPath, backupDir, root); }
    }

    [Fact]
    public void EachRowsRelativePathJoinsBackToTheAbsoluteOne()
    {
        (string dbPath, string backupDir, string root) = NewFixture();
        try
        {
            SeedProjectAndEdges(dbPath, root, outsideRootRows: 0);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                Assert.True(SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir).Success);
                GraphFileRelSchema.BackfillResult backfill = GraphFileRelSchema.Backfill(connection);
                Assert.True(backfill.Filled > 0);
            }

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand cmd = check.CreateCommand();
            cmd.CommandText = "SELECT file, file_rel FROM edges WHERE file_rel IS NOT NULL";
            using SqliteDataReader r = cmd.ExecuteReader();
            int checked_ = 0;
            while (r.Read())
            {
                string file = r.GetString(0);
                string fileRel = r.GetString(1);
                Assert.Equal(file.Replace('\\', '/'), Path.Combine(root, fileRel).Replace('\\', '/'));
                checked_++;
            }
            Assert.True(checked_ > 0);
        }
        finally { Cleanup(dbPath, backupDir, root); }
    }

    [Fact]
    public void ARowOutsideItsProjectRootStaysNullAndIsCounted()
    {
        (string dbPath, string backupDir, string root) = NewFixture();
        try
        {
            SeedProjectAndEdges(dbPath, root, outsideRootRows: 1);
            GraphFileRelSchema.BackfillResult backfill;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                Assert.True(SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir).Success);
                backfill = GraphFileRelSchema.Backfill(connection);
            }
            Assert.Equal(1, backfill.OutsideRoot);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand cmd = check.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM edges WHERE file_rel IS NULL";
            Assert.Equal(1L, (long)(cmd.ExecuteScalar() ?? 0L));
        }
        finally { Cleanup(dbPath, backupDir, root); }
    }

    [Fact]
    public void AProjectRootMovedToAnotherFolderGivesIdenticalGraphAnswers()
    {
        (string dbPath, string backupDir, string root) = NewFixture();
        string movedRoot = Path.Combine(Path.GetTempPath(), $"aitm-file-rel-moved-{Guid.NewGuid():N}");
        try
        {
            SeedProjectAndEdges(dbPath, root, outsideRootRows: 0);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                Assert.True(SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir).Success);
                GraphFileRelSchema.Backfill(connection);
            }

            string beforeMove;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
                beforeMove = new ImpactTool().ExecuteCli(connection, "Widget");

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                using SqliteCommand upd = connection.CreateCommand();
                upd.CommandText = "UPDATE projects SET root=$r WHERE name='web'";
                upd.Parameters.AddWithValue("$r", movedRoot);
                upd.ExecuteNonQuery();
            }

            string afterMove;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
                afterMove = new ImpactTool().ExecuteCli(connection, "Widget");

            Assert.Contains("Widget", beforeMove);
            Assert.Contains("Widget", afterMove);
            Assert.Contains(root.Replace('\\', '/'), beforeMove);
            Assert.Contains(movedRoot.Replace('\\', '/'), afterMove);
            // The graph answer itself (consumer/contract counts) is unaffected by where root points.
            Assert.Equal(
                beforeMove.Split('\n')[0].Split(':')[0],
                afterMove.Split('\n')[0].Split(':')[0]);
        }
        finally { Cleanup(dbPath, backupDir, root); }
    }

    [Fact]
    public void ASecondRunOfTheMigrationChangesNothing()
    {
        (string dbPath, string backupDir, string root) = NewFixture();
        try
        {
            SeedProjectAndEdges(dbPath, root, outsideRootRows: 1);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                Assert.True(SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir).Success);
                GraphFileRelSchema.Backfill(connection);
            }
            List<string> firstPass = ReadFileRelRows(dbPath);

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                SchemaRunResult second = SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir);
                Assert.True(second.Success, second.Error);
                GraphFileRelSchema.BackfillResult backfill = GraphFileRelSchema.Backfill(connection);
                Assert.Equal(0, backfill.Filled); // every fillable row was already filled on the first pass
            }
            List<string> secondPass = ReadFileRelRows(dbPath);

            Assert.Equal(firstPass, secondPass);
        }
        finally { Cleanup(dbPath, backupDir, root); }
    }

    [Fact]
    public void FreshIndexCodeOnATempRepoWritesFileRel()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), $"aitm-file-rel-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "widget.ts"), "export class Widget {}\n");
        string dbPath = Path.Combine(Path.GetTempPath(), $"aitm-file-rel-index-{Guid.NewGuid():N}.db");
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-file-rel-index-backups-{Guid.NewGuid():N}");
        try
        {
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                SchemaRunner.Apply(connection, [new Aitm.Store.Schema.StoreSchema(), new GraphSchema()]);
                new ProjectTool().Execute(connection, "web", projectRoot, "", "*.ts");
            }
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
                new IndexCodeTool().Execute(connection, null, backupDir);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand cmd = check.CreateCommand();
            cmd.CommandText = "SELECT file, file_rel FROM edges WHERE symbol='Widget'";
            using SqliteDataReader r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.False(r.IsDBNull(1));
            Assert.Equal("widget.ts", r.GetString(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(projectRoot, recursive: true);
            foreach (string suffix in new[] { "", "-wal", "-shm" }) { try { File.Delete(dbPath + suffix); } catch (IOException) { } }
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
        }
    }

    // ---- fixture plumbing -------------------------------------------------------------------

    private static (string dbPath, string backupDir, string root) NewFixture()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"aitm-file-rel-{Guid.NewGuid():N}.db");
        string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-file-rel-backups-{Guid.NewGuid():N}");
        string root = Path.Combine(Path.GetTempPath(), $"aitm-file-rel-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return (dbPath, backupDir, root);
    }

    private static void Cleanup(string dbPath, string backupDir, string root)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" }) { try { File.Delete(dbPath + suffix); } catch (IOException) { } }
        if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    // Builds a "user_version 3"-shaped store (Store + Graph tables only, PRAGMA user_version=3, exactly
    // what a real pre-slice-31 store looks like) with a registered project and edges rows: some absolute
    // paths under root (the index-code convention), plus outsideRootRows rows whose file is absolute but
    // NOT under root.
    private static void SeedProjectAndEdges(string dbPath, string root, int outsideRootRows)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        SchemaRunner.Apply(connection, [new Aitm.Store.Schema.StoreSchema(), new GraphSchema()]);
        using (SqliteCommand pragma = connection.CreateCommand()) { pragma.CommandText = "PRAGMA user_version=3"; pragma.ExecuteNonQuery(); }

        using (SqliteCommand proj = connection.CreateCommand())
        {
            proj.CommandText = "INSERT INTO projects(name, root, lang, globs) VALUES('web', $r, 'ts', '*.ts')";
            proj.Parameters.AddWithValue("$r", root);
            proj.ExecuteNonQuery();
        }

        void InsertEdge(string symbol, string file, int line, string usage)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO edges(symbol, contract, project, file, line, usage, hardcoded) VALUES($s,'decl','web',$f,$l,$u,0)";
            insert.Parameters.AddWithValue("$s", symbol);
            insert.Parameters.AddWithValue("$f", file);
            insert.Parameters.AddWithValue("$l", line);
            insert.Parameters.AddWithValue("$u", usage);
            insert.ExecuteNonQuery();
        }

        InsertEdge("Widget", Path.Combine(root, "widget.ts").Replace('\\', '/'), 1, "ts declaration");
        InsertEdge("Widget", Path.Combine(root, "src", "widget2.ts").Replace('\\', '/'), 4, "ts declaration");
        for (int i = 0; i < outsideRootRows; i++)
            InsertEdge("Elsewhere", Path.Combine(Path.GetTempPath(), $"aitm-file-rel-outside-{Guid.NewGuid():N}.ts").Replace('\\', '/'), 1, "ts declaration");
    }

    private static long RowCount(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM edges";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static List<string> ReadFileRelRows(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT symbol, file, file_rel FROM edges ORDER BY symbol, file";
        using SqliteDataReader r = cmd.ExecuteReader();
        List<string> rows = [];
        while (r.Read())
            rows.Add($"{r.GetString(0)}|{r.GetString(1)}|{(r.IsDBNull(2) ? "<null>" : r.GetString(2))}");
        return rows;
    }

    private static void AssertBackupIntegrityOk(string backupPath)
    {
        using SqliteConnection copy = new($"Data Source={backupPath};Mode=ReadOnly");
        copy.Open();
        using SqliteCommand cmd = copy.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", Convert.ToString(cmd.ExecuteScalar()), ignoreCase: true);
    }
}
