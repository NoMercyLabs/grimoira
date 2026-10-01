using System.Text.RegularExpressions;
using Grimoira.TestSupport;
using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Facts.Tests;

public partial class AddToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndUpsertsTheRow()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("add-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("add-new");
        try
        {
            // Oracle: today's grimoira.cs AddCmd() (grimoira.cs:2417).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run(
                $"add --instance {oldInstance} --term add-fixture --value one --category manual --provenance stated");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: AddTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
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
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void LogsTheWhyFlagToTheMutationLog()
    {
        // Oracle: grimoira.cs's AddCmd() (grimoira.cs:2417) logs GetFlag("--why") ?? "manual" to the mutation
        // log via UpsertFact's `why` parameter (grimoira.cs:616) — not the hardcoded "manual" AddTool used
        // to pass regardless of what the caller asked for.
        string instance = GrimoiraCliRunner.NewTestInstance("add-why");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
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
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void DefaultsTheWhyFlagToManualLikeTheOldCli()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("add-why-default");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
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
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void RejectsAnUnknownProvenance()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("add-bad-provenance");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            Assert.Throws<ArgumentException>(() =>
                new AddTool().Execute(connection, "bad-fixture", "[]", "manual", "v", "", "", "made-up"));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static string Normalize(string s) => WhitespaceRun().Replace(s.Trim(), " ");

    [GeneratedRegex(@"\s+", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex WhitespaceRun();
}
