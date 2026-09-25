using System.Text.RegularExpressions;
using Aitm.TestSupport;
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
    public void LogsTheWhyFlagToTheMutationLog()
    {
        // Oracle: aitm.cs's AddCmd() (aitm.cs:2417) logs GetFlag("--why") ?? "manual" to the mutation
        // log via UpsertFact's `why` parameter (aitm.cs:616) — not the hardcoded "manual" AddTool used
        // to pass regardless of what the caller asked for.
        string instance = AitmCliRunner.NewTestInstance("add-why");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new AddTool().Execute(connection, "why-fixture", "[]", "manual", "one", "", "", "stated", "reason X");
            }

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand select = check.CreateCommand();
            select.CommandText = "SELECT why FROM mutations WHERE k='why-fixture' ORDER BY id DESC LIMIT 1";
            object? why = select.ExecuteScalar();
            Assert.Equal("reason X", why);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void DefaultsTheWhyFlagToManualLikeTheOldCli()
    {
        string instance = AitmCliRunner.NewTestInstance("add-why-default");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new AddTool().Execute(connection, "why-default-fixture", "[]", "manual", "one", "", "", "stated");
            }

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand select = check.CreateCommand();
            select.CommandText = "SELECT why FROM mutations WHERE k='why-default-fixture' ORDER BY id DESC LIMIT 1";
            object? why = select.ExecuteScalar();
            Assert.Equal("manual", why);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
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
