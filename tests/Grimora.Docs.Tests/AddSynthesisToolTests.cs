using Grimora.Docs.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Docs.Tests;

public class AddSynthesisToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndStoresTheSynthesisRow()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("add-synthesis-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("add-synthesis-new");
        string dir = MakeFixtureDir("add-synthesis-fixture");
        string bodyFile = MakeBodyFile("add-synthesis-body");
        try
        {
            // Oracle: today's grimora.cs AddSynthesis() (grimora.cs:1063).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run(
                $"add-synthesis --instance {oldInstance} --path \"{dir}\" --from \"{bodyFile}\" --title \"orientation brief\" --sources \"{bodyFile}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: AddSynthesisTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new AddSynthesisTool().Execute(connection, dir, bodyFile, "orientation brief", bodyFile).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Contains("synthesis stored for", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM docs WHERE category='synthesis'";
            Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));
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
    public void MatchesTodaysCliOutputWhenTheBodyIsTooShortToStore()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("add-synthesis-short-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("add-synthesis-short-new");
        string dir = MakeFixtureDir("add-synthesis-short-fixture");
        string bodyFile = Path.Combine(Path.GetTempPath(), $"grimora-add-synthesis-short-{Guid.NewGuid():N}.md");
        File.WriteAllText(bodyFile, "too short");
        try
        {
            // Oracle: today's grimora.cs AddSynthesis() (grimora.cs:1068), the "body too short" branch.
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run(
                $"add-synthesis --instance {oldInstance} --path \"{dir}\" --from \"{bodyFile}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: AddSynthesisTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new AddSynthesisTool().Execute(connection, dir, bodyFile, "", "").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("add-synthesis: body too short to be worth storing.", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM docs";
            Assert.Equal(0L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
            Directory.Delete(dir, recursive: true);
            File.Delete(bodyFile);
        }
    }

    private static string MakeFixtureDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"grimora-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
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
