using Grimora.Docs.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Docs.Tests;

public class ShedSynthesisToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndDeletesOnlyTheSynthesisRow()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("shed-synthesis-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("shed-synthesis-new");
        string dir = MakeFixtureDir("shed-synthesis-fixture");
        string bodyFile = MakeBodyFile("shed-synthesis-body");
        try
        {
            // Oracle: today's grimora.cs ShedSynthesis() (grimora.cs:1144). Also indexes an absorbed doc under
            // the same directory to prove shed-synthesis, unlike shed-doc, leaves it alone.
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            GrimoraCliRunner.Run($"index-docs --instance {oldInstance} --from \"{dir}\"");
            GrimoraCliRunner.Run($"add-synthesis --instance {oldInstance} --path \"{dir}\" --from \"{bodyFile}\"");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"shed-synthesis --instance {oldInstance} --path \"{dir}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ShedSynthesisTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"index-docs --instance {newInstance} --from \"{dir}\"");
            GrimoraCliRunner.Run($"add-synthesis --instance {newInstance} --path \"{dir}\" --from \"{bodyFile}\"");
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new ShedSynthesisTool().Execute(connection, dir).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Contains("shed the synthesis for", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand countSynthesis = check.CreateCommand();
            countSynthesis.CommandText = "SELECT count(*) FROM docs WHERE category='synthesis'";
            Assert.Equal(0L, (long)(countSynthesis.ExecuteScalar() ?? 0L));
            using SqliteCommand countDocs = check.CreateCommand();
            countDocs.CommandText = "SELECT count(*) FROM docs WHERE category<>'synthesis'";
            Assert.Equal(1L, (long)(countDocs.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
            Directory.Delete(dir, recursive: true);
            File.Delete(bodyFile);
        }
    }

    [Fact]
    public void ReportsNoSynthesisForAnUnstoredDirectory()
    {
        string instance = GrimoraCliRunner.NewTestInstance("shed-synthesis-missing");
        string dir = MakeFixtureDir("shed-synthesis-missing-fixture");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");

            (string stdout, int exitCode) = GrimoraCliRunner.Run($"shed-synthesis --instance {instance} --path \"{dir}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new ShedSynthesisTool().Execute(connection, dir).Trim();

            Assert.Equal(expected, actual);
            Assert.StartsWith("no synthesis stored for", actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string MakeFixtureDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimora-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.md"),
            "# shedsynthesistopic\n\nThis section carries the durable knowledge worth absorbing here, long.\n");
        return dir;
    }

    private static string MakeBodyFile(string label)
    {
        string file = Path.Combine(Path.GetTempPath(), $"grimora-{label}-{Guid.NewGuid():N}.md");
        File.WriteAllText(file, string.Concat(Enumerable.Repeat(
            "This orientation brief distills the answer twelve source files agreed on, so the next reader ",
            10)));
        return file;
    }
}
