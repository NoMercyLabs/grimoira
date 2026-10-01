using Grimoira.Memory.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Memory.Tests;

// docs/RESTRUCTURE.md, design checklist "Secrets in outputs": a redact-chat verb that scrubs rows
// already stored. It backs up first (VACUUM INTO), reports counts per kind, and has a --dry-run.
// Every test here runs against a throwaway test-* instance (GrimoiraCliRunner.NewTestInstance), never a
// real store.
public class RedactChatToolTests
{
    [Fact]
    public void BacksUpFirstThenRedactsStoredRowsAndReportsCountsPerKind()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("redact-chat");
        string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            string root = GrimoiraCliRunner.InstanceDir(instance);

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                // Index without redaction happening a second time: write the row directly so the
                // fixture proves redact-chat itself scrubs an already-stored row, not the indexer.
                InsertRawChatRow(connection, "redact-chat-session:k1", jwt);
            }

            string output;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                output = new RedactChatTool().Execute(connection, root, dryRun: false);
            }

            Assert.Contains("backup ->", output);
            Assert.Contains("jwt: 1", output);
            string backupPath = ExtractBackupPath(output);
            Assert.True(File.Exists(backupPath));

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand select = check.CreateCommand();
            select.CommandText = "SELECT text FROM chat WHERE k='redact-chat-session:k1'";
            string stored = (string)select.ExecuteScalar()!;
            Assert.DoesNotContain(jwt, stored);
            Assert.Contains("[redacted:jwt]", stored);

            // The backup, made before redaction, still holds the original secret.
            using SqliteConnection backupCheck = new($"Data Source={backupPath};Mode=ReadOnly");
            backupCheck.Open();
            using SqliteCommand backupSelect = backupCheck.CreateCommand();
            backupSelect.CommandText = "SELECT text FROM chat WHERE k='redact-chat-session:k1'";
            string backedUpText = (string)backupSelect.ExecuteScalar()!;
            Assert.Contains(jwt, backedUpText);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void DryRunReportsCountsButLeavesStoredRowsUnchanged()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("redact-chat-dry-run");
        string ghp = "ghp_1234567890abcdefghijklmnopqrstuvwx";
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            string root = GrimoiraCliRunner.InstanceDir(instance);

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                InsertRawChatRow(connection, "redact-chat-dry-session:k1", $"leaked token {ghp} in this message");
            }

            string output;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                output = new RedactChatTool().Execute(connection, root, dryRun: true);
            }

            Assert.Contains("github: 1", output);
            Assert.Contains("dry run", output);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand select = check.CreateCommand();
            select.CommandText = "SELECT text FROM chat WHERE k='redact-chat-dry-session:k1'";
            string stored = (string)select.ExecuteScalar()!;
            Assert.Contains(ghp, stored);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static void InsertRawChatRow(SqliteConnection connection, string key, string text)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO chat(k,session,ts,role,text) VALUES($k,'redact-chat-session','2026-09-25T12:00:00.000Z','user',$tx)";
        insert.Parameters.AddWithValue("$k", key);
        insert.Parameters.AddWithValue("$tx", text);
        insert.ExecuteNonQuery();
    }

    private static string ExtractBackupPath(string output)
    {
        string line = output.Split('\n').First(l => l.StartsWith("backup ->", StringComparison.Ordinal));
        return line["backup -> ".Length..].Trim();
    }
}
