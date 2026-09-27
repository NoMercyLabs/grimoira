using System.Diagnostics;
using System.Runtime.CompilerServices;
using Aitm.Graph.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Graph.Tests;

// RESTRUCTURE.md slice 14: "index-code.mjs on a fixture repo, pinned edge rows (none today). The C#
// tool must write the same rows." Oracle: today's index-code.mjs run as a child process against a
// throwaway test-* instance; AITM_SKIP_PROJECTS is set explicitly (blank) so this never inherits
// whatever a real dev shell has configured for the live projects.
public class IndexCodeToolTests
{
    [Fact]
    public void MatchesTodaysIndexCodeEdgeRows()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("index-code-old");
        string newInstance = AitmCliRunner.NewTestInstance("index-code-new");
        string root = MakeFixtureProject("index-code-fixture");
        try
        {
            // Oracle: today's index-code.mjs.
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"project --instance {oldInstance} --name web --root \"{root}\" --globs \"*.ts,*.cs\"");
            RunNodeIndexCode(oldInstance);
            List<string> expected = ReadEdgeRows(AitmCliRunner.InstanceDbPath(oldInstance));

            // New: IndexCodeTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
            string backupDir = Path.Combine(Path.GetTempPath(), $"aitm-index-code-backups-{Guid.NewGuid():N}");
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                new ProjectTool().Execute(setup, "web", root, "", "*.ts,*.cs");
            }
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new IndexCodeTool().Execute(connection, null, backupDir);
            }
            List<string> actual = ReadEdgeRows(dbPath);

            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RunNodeIndexCode(string instance)
    {
        ProcessStartInfo psi = new("node", $"\"{IndexCodeScriptPath()}\" --instance {instance} --quiet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot(),
            // Explicit per the card: never let a real dev shell's project-skip list leak into the oracle run.
            EnvironmentVariables = { ["AITM_SKIP_PROJECTS"] = "" },
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("could not start node");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"node index-code.mjs exited {process.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }

    private static List<string> ReadEdgeRows(string dbPath)
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
            rows.Add(string.Join('\u0001', Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i)) ?? "")));
        }
        return rows;
    }

    private static string MakeFixtureProject(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"aitm-{label}-{Guid.NewGuid():N}");
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

    private static string IndexCodeScriptPath() => Path.Combine(RepoRoot(), "index-code.mjs");

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
