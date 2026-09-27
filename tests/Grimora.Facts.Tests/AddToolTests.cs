using System.Text.RegularExpressions;
using Grimora.TestSupport;
using Grimora.Facts.Tools;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Facts.Tests;

public partial class AddToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndUpsertsTheRow()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("add-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("add-new");
        try
        {
            // Oracle: today's grimora.cs AddCmd() (grimora.cs:2417).
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoraCliRunner.Run(
                $"add --instance {oldInstance} --term add-fixture --value one --category manual --provenance stated");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            // New: AddTool.
            GrimoraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void LogsTheWhyFlagToTheMutationLog()
    {
        // Oracle: grimora.cs's AddCmd() (grimora.cs:2417) logs GetFlag("--why") ?? "manual" to the mutation
        // log via UpsertFact's `why` parameter (grimora.cs:616) — not the hardcoded "manual" AddTool used
        // to pass regardless of what the caller asked for.
        string instance = GrimoraCliRunner.NewTestInstance("add-why");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
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
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void DefaultsTheWhyFlagToManualLikeTheOldCli()
    {
        string instance = GrimoraCliRunner.NewTestInstance("add-why-default");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
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
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void RejectsAnUnknownProvenance()
    {
        string instance = GrimoraCliRunner.NewTestInstance("add-bad-provenance");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            Assert.Throws<ArgumentException>(() =>
                new AddTool().Execute(connection, "bad-fixture", "[]", "manual", "v", "", "", "made-up"));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static string Normalize(string s) => WhitespaceRun().Replace(s.Trim(), " ");

    [GeneratedRegex(@"\s+", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex WhitespaceRun();
}
