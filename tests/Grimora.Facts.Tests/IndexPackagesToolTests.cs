using Grimora.Facts.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Facts.Tests;

// RESTRUCTURE.md section 2.1 (corrected by slice 11b): index-packages writes facts (UpsertFact,
// grimora.cs:821), so it belongs in Grimora.Facts, not Grimora.Graph.
public class IndexPackagesToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndWritesOneFactPerPackage()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("index-packages-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("index-packages-new");
        string dir = MakeFixtureDir("index-packages-fixture");
        try
        {
            // Oracle: today's grimora.cs IndexPackages() (grimora.cs:799).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"index-packages --instance {oldInstance} --root \"{dir}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: IndexPackagesTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new IndexPackagesTool().Execute(connection, dir).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("indexed 1 package(s) from " + dir + ".", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand select = check.CreateCommand();
            select.CommandText = "SELECT value, category, source FROM facts WHERE k='index-packages-fixture-pkg'";
            using SqliteDataReader reader = select.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Contains("index-packages-fixture-pkg v1.2.3", reader.GetString(0));
            Assert.Contains("a fixture package", reader.GetString(0));
            Assert.Equal("package", reader.GetString(1));
            Assert.Equal("package.json", reader.GetString(2));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SkipsPackageJsonUnderNodeModules()
    {
        string instance = GrimoraCliRunner.NewTestInstance("index-packages-skip");
        string dir = MakeFixtureDir("index-packages-skip-fixture");
        string nodeModules = Path.Combine(dir, "node_modules", "some-dep");
        Directory.CreateDirectory(nodeModules);
        File.WriteAllText(Path.Combine(nodeModules, "package.json"), "{\"name\":\"some-dep\",\"version\":\"9.9.9\"}");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            string actual = new IndexPackagesTool().Execute(connection, dir).Trim();
            Assert.Equal("indexed 1 package(s) from " + dir + ".", actual);

            using SqliteCommand select = connection.CreateCommand();
            select.CommandText = "SELECT count(*) FROM facts WHERE k='some-dep'";
            Assert.Equal(0L, (long)(select.ExecuteScalar() ?? 0L));
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
        File.WriteAllText(Path.Combine(dir, "package.json"),
            "{\"name\":\"index-packages-fixture-pkg\",\"version\":\"1.2.3\",\"description\":\"a fixture package\"}");
        return dir;
    }
}
