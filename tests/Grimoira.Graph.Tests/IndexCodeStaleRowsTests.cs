using Grimoira.Graph.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Graph.Tests;

// 2026-10-05: index-code only ever ADDED rows. After the NoMercy workspace move, 94,473 of 185,068 live
// edges (51%) still pointed at 14,503 files that no longer existed, so graph-query answered with gone
// paths. A re-index must replace a project's own declaration rows: rows for files that are gone or
// re-scanned go, curated (seed-edges) rows and other projects' rows stay.
public class IndexCodeStaleRowsTests
{
    [Fact]
    public void ReindexDropsDeclarationRowsForFilesThatNoLongerExist()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("index-code-stale");
        string root = MakeFixtureProject("index-code-stale");
        string backupDir = Path.Combine(Path.GetTempPath(), $"grimoira-index-code-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = OpenFreshStore(instance, backupDir);
            new ProjectTool().Execute(connection, "web", root, "", "*.ts,*.cs");
            new IndexCodeTool().Execute(connection, null, backupDir);
            Assert.Contains("WidgetService", Symbols(connection, "web"));

            File.Delete(Path.Combine(root, "Service.cs"));
            File.WriteAllText(Path.Combine(root, "Other.cs"), "public class OtherService\n{\n}\n");
            new IndexCodeTool().Execute(connection, "web", backupDir);

            List<string> after = Symbols(connection, "web");
            Assert.DoesNotContain("WidgetService", after);
            Assert.DoesNotContain("GetWidget", after);
            Assert.Contains("OtherService", after);
            Assert.Contains("Widget", after);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReindexKeepsCuratedRowsAndOtherProjectsRows()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("index-code-keeps-curated");
        string root = MakeFixtureProject("index-code-keeps-curated");
        string backupDir = Path.Combine(Path.GetTempPath(), $"grimoira-index-code-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = OpenFreshStore(instance, backupDir);
            new ProjectTool().Execute(connection, "web", root, "", "*.ts,*.cs");
            // A curated usage edge for "web" (what seed-edges writes) and a stale decl row of ANOTHER
            // project, both pointing at files that do not exist. Only index-code's own rows for the
            // scanned project may be replaced.
            InsertEdge(connection, "CuratedSymbol", "usage", "web", root.Replace('\\', '/') + "/gone/curated.ts", 3, "curated usage");
            InsertEdge(connection, "OtherProjectSymbol", "decl", "other", "/repo/other/gone.ts", 1, "ts declaration");
            InsertEdge(connection, "StaleWebSymbol", "decl", "web", root.Replace('\\', '/') + "/gone/stale.ts", 1, "ts declaration");

            new IndexCodeTool().Execute(connection, "web", backupDir);

            Assert.Contains("CuratedSymbol", Symbols(connection, "web"));
            Assert.Contains("OtherProjectSymbol", Symbols(connection, "other"));
            Assert.DoesNotContain("StaleWebSymbol", Symbols(connection, "web"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(root, recursive: true);
        }
    }

    // The live store held the same file twice, once as c:/Projects/... and once as C:/Projects/...,
    // because the project root was registered with either casing. One file gets one path.
    [Fact]
    public void ReindexStoresOneNormalisedPathPerFile()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("index-code-drive-letter");
        string root = MakeFixtureProject("index-code-drive-letter");
        string backupDir = Path.Combine(Path.GetTempPath(), $"grimoira-index-code-backups-{Guid.NewGuid():N}");
        try
        {
            using SqliteConnection connection = OpenFreshStore(instance, backupDir);

            string normalised = root.Replace('\\', '/');
            if (normalised.Length > 1 && normalised[1] == ':')
                normalised = char.ToUpperInvariant(normalised[0]) + normalised[1..];
            // A pre-existing row for the same file with the other spelling (lower-case drive, backslashes).
            string otherSpelling = root.Length > 1 && root[1] == ':'
                ? char.ToLowerInvariant(root[0]) + root[1..].Replace('/', '\\')
                : root.Replace('/', '\\');
            InsertEdge(connection, "WidgetService", "decl", "web", otherSpelling + "\\Service.cs", 2, "csharp declaration");
            string registeredRoot = root.Length > 1 && root[1] == ':' ? char.ToLowerInvariant(root[0]) + root[1..] : root;
            new ProjectTool().Execute(connection, "web", registeredRoot, "", "*.ts,*.cs");

            new IndexCodeTool().Execute(connection, "web", backupDir);

            List<string> files = Files(connection, "web", "WidgetService");
            Assert.Single(files);
            Assert.Equal(normalised + "/Service.cs", files[0]);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(root, recursive: true);
        }
    }

    // No CLI `init` here: GrimoiraCliRunner only replays frozen goldens. StoreSchema (schema_steps) then
    // GraphSchema gives the same empty edges/projects tables a fresh instance has.
    internal static SqliteConnection OpenFreshStore(string instance, string backupDir)
    {
        string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        SqliteConnection connection = StoreConnection.Open(dbPath);
        Grimoira.Store.Schema.SchemaRunResult setup = Grimoira.Store.Schema.SchemaRunner.Run(
            connection, [new Grimoira.Store.Schema.StoreSchema(), new Grimoira.Graph.Schema.GraphSchema()], backupDir);
        Assert.True(setup.Success, setup.Error);
        return connection;
    }

    internal static void InsertEdge(SqliteConnection connection, string symbol, string contract, string project, string file, int line, string usage)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,0)";
        insert.Parameters.AddWithValue("$s", symbol);
        insert.Parameters.AddWithValue("$c", contract);
        insert.Parameters.AddWithValue("$p", project);
        insert.Parameters.AddWithValue("$f", file);
        insert.Parameters.AddWithValue("$l", line);
        insert.Parameters.AddWithValue("$u", usage);
        insert.ExecuteNonQuery();
    }

    internal static List<string> Symbols(SqliteConnection connection, string project)
    {
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "SELECT symbol FROM edges WHERE project=$p ORDER BY symbol";
        read.Parameters.AddWithValue("$p", project);
        using SqliteDataReader r = read.ExecuteReader();
        List<string> rows = [];
        while (r.Read()) rows.Add(r.GetString(0));
        return rows;
    }

    internal static List<string> Files(SqliteConnection connection, string project, string symbol)
    {
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "SELECT file FROM edges WHERE project=$p AND symbol=$s ORDER BY file";
        read.Parameters.AddWithValue("$p", project);
        read.Parameters.AddWithValue("$s", symbol);
        using SqliteDataReader r = read.ExecuteReader();
        List<string> rows = [];
        while (r.Read()) rows.Add(r.GetString(0));
        return rows;
    }

    internal static string MakeFixtureProject(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimoira-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.ts"),
            "export class Widget {\n  constructor() {}\n}\nexport function renderWidget() {\n  return 1;\n}\n");
        File.WriteAllText(Path.Combine(dir, "Service.cs"),
            "namespace Fixture;\npublic class WidgetService\n{\n    public string GetWidget()\n    {\n        return \"x\";\n    }\n}\n");
        return dir;
    }
}
