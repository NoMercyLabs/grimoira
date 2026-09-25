using System.Text.RegularExpressions;
using Aitm.Store.Data;
using Aitm.Store.Tests.Support;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

public class ImportToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndFactCounts()
    {
        string sourceInstance = AitmCliRunner.NewTestInstance("import-source");
        string oldDestInstance = AitmCliRunner.NewTestInstance("import-old-dest");
        string newDestInstance = AitmCliRunner.NewTestInstance("import-new-dest");
        try
        {
            AitmCliRunner.Run($"init --instance {sourceInstance}");
            AitmCliRunner.Run($"add --instance {sourceInstance} --term import-fixture-one --value one --category manual");
            AitmCliRunner.Run($"add --instance {sourceInstance} --term import-fixture-two --value two --category manual");
            string sourceDbPath = AitmCliRunner.InstanceDbPath(sourceInstance);

            // Oracle: today's aitm.cs Import() (aitm.cs:710-737).
            AitmCliRunner.Run($"init --instance {oldDestInstance}");
            (string stdout, int exitCode) = AitmCliRunner.Run($"import --instance {oldDestInstance} --from \"{sourceDbPath}\"");
            Assert.Equal(0, exitCode);
            string expectedPrefix = MessagePrefix(stdout);

            // New: ImportTool.
            AitmCliRunner.Run($"init --instance {newDestInstance}");
            string newDestDbPath = AitmCliRunner.InstanceDbPath(newDestInstance);
            string message;
            using (SqliteConnection connection = StoreConnection.Open(newDestDbPath))
            {
                message = new ImportTool().Execute(connection, sourceDbPath, newDestDbPath);
            }
            string actualPrefix = MessagePrefix(message);

            Assert.Equal(expectedPrefix, actualPrefix);
            Assert.Equal("imported 2 rows -> 2 current facts, 2 mutations logged.", actualPrefix);

            using SqliteConnection check = new($"Data Source={newDestDbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM facts";
            Assert.Equal(2L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(sourceInstance);
            AitmCliRunner.DeleteInstance(oldDestInstance);
            AitmCliRunner.DeleteInstance(newDestInstance);
        }
    }

    // Strips the trailing "(<db path>)" so two different instance directories still compare equal.
    private static string MessagePrefix(string text) => Regex.Replace(text.Trim(), @"\s*\([^)]*\)\s*$", "");
}
