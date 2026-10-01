using System.Text.RegularExpressions;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Store.Tests;

public partial class ImportToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndFactCounts()
    {
        string sourceInstance = GrimoiraCliRunner.NewTestInstance("import-source");
        string oldDestInstance = GrimoiraCliRunner.NewTestInstance("import-old-dest");
        string newDestInstance = GrimoiraCliRunner.NewTestInstance("import-new-dest");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {sourceInstance}");
            GrimoiraCliRunner.Run($"add --instance {sourceInstance} --term import-fixture-one --value one --category manual");
            GrimoiraCliRunner.Run($"add --instance {sourceInstance} --term import-fixture-two --value two --category manual");
            string sourceDbPath = GrimoiraCliRunner.InstanceDbPath(sourceInstance);

            // Oracle: today's grimoira.cs Import() (grimoira.cs:710-737).
            GrimoiraCliRunner.Run($"init --instance {oldDestInstance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"import --instance {oldDestInstance} --from \"{sourceDbPath}\"");
            Assert.Equal(0, exitCode);
            string expectedPrefix = MessagePrefix(stdout);

            // New: ImportTool.
            GrimoiraCliRunner.Run($"init --instance {newDestInstance}");
            string newDestDbPath = GrimoiraCliRunner.InstanceDbPath(newDestInstance);
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
            GrimoiraCliRunner.DeleteInstance(sourceInstance);
            GrimoiraCliRunner.DeleteInstance(oldDestInstance);
            GrimoiraCliRunner.DeleteInstance(newDestInstance);
        }
    }

    // Strips the trailing "(<db path>)" so two different instance directories still compare equal.
    private static string MessagePrefix(string text) => TrailingParenthesisedSuffix().Replace(text.Trim(), "");

    [GeneratedRegex(@"\s*\([^)]*\)\s*$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex TrailingParenthesisedSuffix();
}
