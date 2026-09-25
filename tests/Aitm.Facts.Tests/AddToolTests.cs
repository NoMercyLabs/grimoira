using System.Text.RegularExpressions;
using Aitm.Facts.Tests.Support;
using Aitm.Facts.Tools;
using Aitm.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Facts.Tests;

public class AddToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndUpsertsTheRow()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("add-old");
        string newInstance = AitmCliRunner.NewTestInstance("add-new");
        try
        {
            // Oracle: today's aitm.cs AddCmd() (aitm.cs:2417).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = AitmCliRunner.Run(
                $"add --instance {oldInstance} --term add-fixture --value one --category manual --provenance stated");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: AddTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = Normalize(new AddTool().Execute(connection, "add-fixture", "[]", "manual", "one", "", "", "stated"));
            }

            Assert.Equal(expected, actual);
            Assert.Equal("added/updated 'add-fixture' [stated] (logged to mutations).", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand select = check.CreateCommand();
            select.CommandText = "SELECT value, provenance FROM facts WHERE k='add-fixture'";
            using SqliteDataReader reader = select.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("one", reader.GetString(0));
            Assert.Equal("stated", reader.GetString(1));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void RejectsAnUnknownProvenance()
    {
        string instance = AitmCliRunner.NewTestInstance("add-bad-provenance");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            Assert.Throws<ArgumentException>(() =>
                new AddTool().Execute(connection, "bad-fixture", "[]", "manual", "v", "", "", "made-up"));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string Normalize(string s) => Regex.Replace(s.Trim(), @"\s+", " ");
}
