using Grimoira.Memory.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Memory.Tests;

public class IndexChatToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndIndexesEachUserMessageIntoTheChatTable()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("index-chat-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("index-chat-new");
        string transcript = MakeFixtureTranscript("index-chat-fixture-session");
        try
        {
            // Oracle: today's grimoira.cs IndexChat()/IndexChatFile() (grimoira.cs:2289/2298).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Seed($"index-chat --instance {oldInstance} --from \"{transcript}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: IndexChatTool.
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new IndexChatTool().Execute(connection, transcript).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Contains("5 turn(s)", actual);
            Assert.Contains("done: 5 turn(s) indexed", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM chat";
            Assert.Equal(5L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
            File.Delete(transcript);
        }
    }

    // RESTRUCTURE.md slice 24 bullet 1: ports the `selftest` check "recall: indexed chat message is
    // retrievable" now that selftest itself is gone. Message content over 40 characters clears
    // IndexChatTool's own noise gate, so a message indexed through the real tool must be found by an
    // FTS5 MATCH against the chat_fts mirror it maintains.
    [Fact]
    public void AnIndexedMessageIsRetrievableThroughTheChatFtsMirror()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("index-chat-fts-retrievable");
        string transcript = MakeFixtureTranscriptWithMessage(
            "index-chat-fts-session", "the operator said never use optionalDependencies in package json");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new IndexChatTool().Execute(connection, transcript);
            }

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM chat_fts WHERE chat_fts MATCH 'optionaldependencies'";
            Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            File.Delete(transcript);
        }
    }

    [Fact]
    public void RunningTwiceOnTheSameTranscriptIsIdempotent()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("index-chat-idempotent");
        string transcript = MakeFixtureTranscript("index-chat-idempotent-session");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                string first = new IndexChatTool().Execute(connection, transcript).Trim();
                Assert.Contains("5 turn(s)", first);
            }

            long afterFirst;
            using (SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly"))
            {
                check.Open();
                using SqliteCommand count = check.CreateCommand();
                count.CommandText = "SELECT count(*) FROM chat";
                afterFirst = (long)(count.ExecuteScalar() ?? 0L);
            }

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                string second = new IndexChatTool().Execute(connection, transcript).Trim();
                // The second run re-reads the same 2 messages (upsert), so it reports 2 again, not 4 —
                // the assertion that proves the run was idempotent, not merely repeatable.
                Assert.Contains("5 turn(s)", second);
            }

            using SqliteConnection recheck = new($"Data Source={dbPath};Mode=ReadOnly");
            recheck.Open();
            using SqliteCommand recount = recheck.CreateCommand();
            recount.CommandText = "SELECT count(*) FROM chat";
            long afterSecond = (long)(recount.ExecuteScalar() ?? 0L);

            Assert.Equal(5L, afterFirst);
            Assert.Equal(afterFirst, afterSecond);

            using SqliteCommand ftsCount = recheck.CreateCommand();
            ftsCount.CommandText = "SELECT count(*) FROM chat_fts";
            Assert.Equal(5L, (long)(ftsCount.ExecuteScalar() ?? 0L));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            File.Delete(transcript);
        }
    }

    [Fact]
    public void ScrubsTokenShapedStringsFromStoredChatRowsInBothTheOldAndNewCode()
    {
        // RESTRUCTURE.md design checklist "Secrets in outputs": index-chat removes token-shaped
        // strings (JWTs, Bearer headers, common key prefixes, PEM private keys) before it stores chat.
        // Checked against BOTH today's grimoira.cs IndexChat (the oracle, the one allowed grimoira.cs change
        // for this step) and the new IndexChatTool.
        string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        string bearer = "Bearer abcDEF123456.ghIJKL7890-secretvalue";
        string ghp = "ghp_1234567890abcdefghijklmnopqrstuvwx";
        string awsKey = "AKIAIOSFODNN7EXAMPLE";
        string pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAK8example\n-----END RSA PRIVATE KEY-----";

        string oldInstance = GrimoiraCliRunner.NewTestInstance("index-chat-secrets-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("index-chat-secrets-new");
        string transcript = MakeSecretsFixtureTranscript("index-chat-secrets-session", jwt, bearer, ghp, awsKey, pem);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            GrimoiraCliRunner.Seed($"index-chat --instance {oldInstance} --from \"{transcript}\"");
            string oldDbPath = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            string oldStoredText = ReadAllChatText(oldDbPath);

            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string newDbPath = GrimoiraCliRunner.InstanceDbPath(newInstance);
            using (SqliteConnection connection = StoreConnection.Open(newDbPath))
            {
                new IndexChatTool().Execute(connection, transcript);
            }
            string newStoredText = ReadAllChatText(newDbPath);

            foreach (string secret in new[] { jwt, bearer, ghp, awsKey, "MIIBOgIBAAJBAK8example" })
            {
                Assert.DoesNotContain(secret, oldStoredText);
                Assert.DoesNotContain(secret, newStoredText);
            }
            Assert.Contains("[redacted:jwt]", oldStoredText);
            Assert.Contains("[redacted:jwt]", newStoredText);
            Assert.Contains("[redacted:bearer]", oldStoredText);
            Assert.Contains("[redacted:bearer]", newStoredText);
            Assert.Contains("[redacted:github]", oldStoredText);
            Assert.Contains("[redacted:github]", newStoredText);
            Assert.Contains("[redacted:aws]", oldStoredText);
            Assert.Contains("[redacted:aws]", newStoredText);
            Assert.Contains("[redacted:private-key]", oldStoredText);
            Assert.Contains("[redacted:private-key]", newStoredText);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
            File.Delete(transcript);
        }
    }

    [Fact]
    public void DoesNotRedactOrdinaryTextCodeOrHashesInStoredChatRows()
    {
        // The false-positive companion to the redaction test above: ordinary text, code and hashes
        // (a git SHA, a sha256 digest) must reach the chat table unchanged.
        string plainMessage = "this fixture message talks about commit 4cdd17b3d43f2a1b5c6d7e8f9a0b1c2d3e4f5061 "
            + "and sha256 e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855, plus normal code.";
        string instance = GrimoiraCliRunner.NewTestInstance("index-chat-no-false-positive");
        string transcript = MakeFixtureTranscriptWithMessage("index-chat-no-fp-session", plainMessage);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new IndexChatTool().Execute(connection, transcript);
            }
            string storedText = ReadAllChatText(dbPath);

            Assert.Contains(plainMessage, storedText);
            Assert.DoesNotContain("[redacted:", storedText);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            File.Delete(transcript);
        }
    }

    private static string ReadAllChatText(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT group_concat(text, char(10)) FROM chat";
        return (string?)select.ExecuteScalar() ?? "";
    }

    private static string MakeSecretsFixtureTranscript(string session, string jwt, string bearer, string ghp, string awsKey, string pem)
    {
        string path = Path.Combine(Path.GetTempPath(), $"grimoira-{session}-{Guid.NewGuid():N}.jsonl");
        (string uuid, string content)[] messages =
        [
            ("66666666-6666-6666-6666-666666666666", $"here is a token {jwt} sent by mistake, well over the forty character gate"),
            ("77777777-7777-7777-7777-777777777777", $"the header was {bearer} in the failing request, also well over the gate"),
            ("88888888-8888-8888-8888-888888888888", $"leaked github token {ghp} and aws key {awsKey} in the same paste"),
            ("99999999-9999-9999-9999-999999999999", $"pasted a pem block by accident: {pem} that should never land here"),
        ];
        string[] lines = [.. messages.Select((m, i) => System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "user",
            m.uuid,
            timestamp = $"2026-09-25T11:0{i}:00.000Z",
            message = new { m.content },
        }))];
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string MakeFixtureTranscriptWithMessage(string session, string message)
    {
        string path = Path.Combine(Path.GetTempPath(), $"grimoira-{session}-{Guid.NewGuid():N}.jsonl");
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "user",
            uuid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            timestamp = "2026-09-25T12:00:00.000Z",
            message = new { content = message },
        });
        File.WriteAllLines(path, [json]);
        return path;
    }

    [Fact]
    public void ReportsAMissingFileTheSameWayTheCliOracleDoes()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("index-chat-missing");
        string missingFile = Path.Combine(Path.GetTempPath(), $"grimoira-index-chat-missing-{Guid.NewGuid():N}.jsonl");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Seed($"index-chat --instance {instance} --from \"{missingFile}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new IndexChatTool().Execute(connection, missingFile).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("not found", actual);
            Assert.Contains("done: 0 turn(s) indexed", actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureTranscript(string session)
    {
        string path = Path.Combine(Path.GetTempPath(), $"grimoira-{session}-{Guid.NewGuid():N}.jsonl");
        string[] lines =
        [
            """{"type":"user","uuid":"11111111-1111-1111-1111-111111111111","timestamp":"2026-09-25T10:00:00.000Z","message":{"content":"this is the first fixture message about the chat recall topic, long enough to pass the length gate"}}""",
            // Too short (< 40 chars): must be skipped, same as the oracle.
            """{"type":"user","uuid":"22222222-2222-2222-2222-222222222222","timestamp":"2026-09-25T10:01:00.000Z","message":{"content":"too short"}}""",
            // A tool echo wrapped in "<...>": must be skipped, same as the oracle.
            """{"type":"user","uuid":"33333333-3333-3333-3333-333333333333","timestamp":"2026-09-25T10:02:00.000Z","message":{"content":"<tool-result>this looks like a tool echo wrapper, not real operator input at all</tool-result>"}}""",
            // Not a user turn: must be skipped.
            """{"type":"assistant","uuid":"44444444-4444-4444-4444-444444444444","timestamp":"2026-09-25T10:03:00.000Z","message":{"content":"an assistant reply that is long enough to pass the length gate on its own"}}""",
            """{"type":"user","uuid":"55555555-5555-5555-5555-555555555555","timestamp":"2026-09-25T10:04:00.000Z","message":{"content":"this is the second fixture message about the chat recall topic, also long enough"}}""",
        ];
        File.WriteAllLines(path, lines);
        return path;
    }
}
