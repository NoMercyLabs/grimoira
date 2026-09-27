using System.Runtime.CompilerServices;
using Grimora.Graph.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Graph.Tests;

// RESTRUCTURE.md slice 14: "index-code.mjs on a fixture repo, pinned edge rows. The C# tool must write the
// same rows." The oracle (index-code.mjs) is gone; Goldens/index-code-fixture-edges.tsv holds the 4 rows it
// wrote for this fixture, produced by running index-code.mjs itself (commit a573bc1) and not by IndexCodeTool.
// The fixture root varies per run, so it is written as <root> in both the golden and the compared rows.
public class IndexCodeToolTests
{
    [Fact]
    public void MatchesTheFrozenIndexCodeEdgeRows()
    {
        string newInstance = GrimoraCliRunner.NewTestInstance("index-code-new");
        string root = MakeFixtureProject("index-code-fixture");
        try
        {
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
            string backupDir = Path.Combine(Path.GetTempPath(), $"grimora-index-code-backups-{Guid.NewGuid():N}");
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                new ProjectTool().Execute(setup, "web", root, "", "*.ts,*.cs");
            }

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new IndexCodeTool().Execute(connection, null, backupDir);
            }

            List<string> actual = ReadEdgeRows(dbPath, root);
            List<string> golden = [.. File.ReadAllLines(GoldenPath())];

            Assert.Equal(4, golden.Count);
            Assert.Equal(golden, actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(newInstance);
            Directory.Delete(root, recursive: true);
        }
    }

    private static List<string> ReadEdgeRows(string dbPath, string root)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT symbol,contract,project,file,line,usage,hardcoded FROM edges ORDER BY symbol,file,line";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(string.Join('\t', Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "<null>" : Normalise(Convert.ToString(reader.GetValue(i)) ?? "", root))));
        }
        return rows;
    }

    // The tool stores absolute paths with forward slashes; the fixture root differs on every run.
    private static string Normalise(string value, string root) =>
        value.Replace(root.Replace('\\', '/'), "<root>", StringComparison.OrdinalIgnoreCase)
            .Replace(root, "<root>", StringComparison.OrdinalIgnoreCase);

    // grimora/issues/2: a run against an instance with zero registered projects (a fresh instance, or a
    // wrong/typo'd one — see CliDispatchTests.IndexCodeRejectsAStrayPositionalArgument...) must not print
    // the same "total new edges: 0" a real, already-fully-indexed instance prints. Otherwise the two
    // cases are indistinguishable from the output alone, which is exactly how issue #2 went unnoticed.
    [Fact]
    public void ReportsClearlyWhenNoProjectsAreRegisteredInsteadOfAPlainZero()
    {
        string instance = GrimoraCliRunner.NewTestInstance("index-code-no-projects");
        try
        {
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            string backupDir = Path.Combine(Path.GetTempPath(), $"grimora-index-code-backups-{Guid.NewGuid():N}");

            string result;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                // StoreSchema first (schema_steps, which RecordStep needs) then bare GraphSchema, no
                // ProjectTool call: this instance genuinely has zero registered projects, the case this
                // test is about.
                Grimora.Store.Schema.SchemaRunResult setup = Grimora.Store.Schema.SchemaRunner.Run(
                    connection, [new Grimora.Store.Schema.StoreSchema(), new Grimora.Graph.Schema.GraphSchema()], backupDir);
                Assert.True(setup.Success, setup.Error);
                result = new IndexCodeTool().Execute(connection, null, backupDir);
            }

            Assert.Contains("no registered projects for this instance", result);
            Assert.DoesNotContain("total new edges: 0", result);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureProject(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimora-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "widget.ts"),
            "export class Widget {\n  constructor() {}\n}\nexport function renderWidget() {\n  return 1;\n}\n");
        File.WriteAllText(Path.Combine(dir, "Service.cs"),
            "namespace Fixture;\npublic class WidgetService\n{\n    public string GetWidget()\n    {\n        return \"x\";\n    }\n}\n");
        string nodeModules = Path.Combine(dir, "node_modules", "ignored-pkg");
        Directory.CreateDirectory(nodeModules);
        File.WriteAllText(Path.Combine(nodeModules, "index.ts"), "export class ShouldNeverBeIndexed {}\n");
        return dir;
    }

    private static string GoldenPath([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "Goldens", "index-code-fixture-edges.tsv");
}
