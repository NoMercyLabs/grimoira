using System.Text.RegularExpressions;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Store.Tests;

public partial class ImportToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndFactCounts()
    {
        string sourceInstance = GrimoraCliRunner.NewTestInstance("import-source");
        string oldDestInstance = GrimoraCliRunner.NewTestInstance("import-old-dest");
        string newDestInstance = GrimoraCliRunner.NewTestInstance("import-new-dest");
        try
        {
            GrimoraCliRunner.Run($"init --instance {sourceInstance}");
            GrimoraCliRunner.Run($"add --instance {sourceInstance} --term import-fixture-one --value one --category manual");
            GrimoraCliRunner.Run($"add --instance {sourceInstance} --term import-fixture-two --value two --category manual");
            string sourceDbPath = GrimoraCliRunner.InstanceDbPath(sourceInstance);

            // Oracle: today's grimora.cs Import() (grimora.cs:710-737).
            GrimoraCliRunner.Run($"init --instance {oldDestInstance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"import --instance {oldDestInstance} --from \"{sourceDbPath}\"");
            Assert.Equal(0, exitCode);
            string expectedPrefix = MessagePrefix(stdout);

            // New: ImportTool.
            GrimoraCliRunner.Run($"init --instance {newDestInstance}");
            string newDestDbPath = GrimoraCliRunner.InstanceDbPath(newDestInstance);
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
            GrimoraCliRunner.DeleteInstance(sourceInstance);
            GrimoraCliRunner.DeleteInstance(oldDestInstance);
            GrimoraCliRunner.DeleteInstance(newDestInstance);
        }
    }

    // Strips the trailing "(<db path>)" so two different instance directories still compare equal.
    private static string MessagePrefix(string text) => TrailingParenthesisedSuffix().Replace(text.Trim(), "");

    [GeneratedRegex(@"\s*\([^)]*\)\s*$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex TrailingParenthesisedSuffix();
}
