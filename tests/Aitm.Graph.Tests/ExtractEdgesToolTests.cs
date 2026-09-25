using Aitm.Graph.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Graph.Tests;

public class ExtractEdgesToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndStagesOneCandidatePerConsumingFile()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("extract-edges-old");
        string newInstance = AitmCliRunner.NewTestInstance("extract-edges-new");
        string root = MakeFixtureProject("extract-edges-fixture");
        try
        {
            // Oracle: today's aitm.cs ExtractEdges() (aitm.cs:2830).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"project --instance {oldInstance} --name web --root \"{root}\" --globs \"*.ts\"");
            (string stdout, int exitCode) = AitmCliRunner.Run($"extract-edges --instance {oldInstance} --symbol has_more --contract PaginatedResponse");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ExtractEdgesTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                new ProjectTool().Execute(setup, "web", root, "", "*.ts");
            }
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new ExtractEdgesTool().Execute(connection, "has_more", "PaginatedResponse").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("extracted 1 candidate consumer(s) of 'has_more'. Review: aitm candidates --symbol has_more   then: aitm promote <id>", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM edge_candidates WHERE symbol='has_more' AND status='pending'";
            Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReportsToRegisterAProjectWhenNoneAreRegistered()
    {
        string instance = AitmCliRunner.NewTestInstance("extract-edges-no-projects");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = AitmCliRunner.Run($"extract-edges --instance {instance} --symbol has_more");
            Assert.Equal(0, exitCode);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = Normalize(new ExtractEdgesTool().Execute(connection, "has_more", ""));

            Assert.Equal(Normalize(stdout), actual);
            Assert.Equal("no projects registered - add one: aitm project --name web --root <path> --globs \"*.ts,*.vue\"", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` check "extract: token match respects word
    // boundaries" now that selftest itself is gone. "data" must not match inside "metadata" but must
    // match the whole-token "data" in "row.data".
    [Fact]
    public void SymbolMatchRespectsWordBoundariesAndNeverMatchesInsideALongerIdentifier()
    {
        string instance = AitmCliRunner.NewTestInstance("extract-edges-word-boundary");
        string root = Path.Combine(Path.GetTempPath(), $"aitm-extract-edges-boundary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "metadata-only.ts"), "const metadata = 1;\n");
            File.WriteAllText(Path.Combine(root, "real-usage.ts"), "row.data = 1;\n");

            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using (SqliteConnection setup = StoreConnection.Open(dbPath))
            {
                new ProjectTool().Execute(setup, "web", root, "", "*.ts");
            }

            string result;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                result = new ExtractEdgesTool().Execute(connection, "data", "");
            }

            Assert.Equal("extracted 1 candidate consumer(s) of 'data'. Review: aitm candidates --symbol data   then: aitm promote <id>", result);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM edge_candidates WHERE symbol='data' AND file LIKE '%real-usage.ts'";
            Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string MakeFixtureProject(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"aitm-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "list.ts"), "export const hasMoreFlag = page.has_more;\n");
        File.WriteAllText(Path.Combine(dir, "unrelated.ts"), "export const nothing = 1;\n");
        return dir;
    }

    // Windows redirects the child's stdout through the OEM codepage, not UTF-8, so aitm.cs's "—" arrives
    // mangled regardless of the encoding this side decodes with — a capture artifact, not a behaviour
    // difference (QueryToolTests documents the same substitution for its own dashes/bullets).
    private static string Normalize(string s) => s.Trim().Replace('—', '-');
}
