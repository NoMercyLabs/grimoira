using Aitm.Memory.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Memory.Tests;

public class IndexChatToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndIndexesEachUserMessageIntoTheChatTable()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("index-chat-old");
        string newInstance = AitmCliRunner.NewTestInstance("index-chat-new");
        string transcript = MakeFixtureTranscript("index-chat-fixture-session");
        try
        {
            // Oracle: today's aitm.cs IndexChat()/IndexChatFile() (aitm.cs:2289/2298).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = AitmCliRunner.Run($"index-chat --instance {oldInstance} --from \"{transcript}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: IndexChatTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new IndexChatTool().Execute(connection, transcript).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Contains("2 message(s)", actual);
            Assert.Contains("done: 2 user message(s) indexed", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM chat";
            Assert.Equal(2L, (long)(count.ExecuteScalar() ?? 0L));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
            File.Delete(transcript);
        }
    }

    [Fact]
    public void RunningTwiceOnTheSameTranscriptIsIdempotent()
    {
        string instance = AitmCliRunner.NewTestInstance("index-chat-idempotent");
        string transcript = MakeFixtureTranscript("index-chat-idempotent-session");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                string first = new IndexChatTool().Execute(connection, transcript).Trim();
                Assert.Contains("2 message(s)", first);
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
                Assert.Contains("2 message(s)", second);
            }

            using SqliteConnection recheck = new($"Data Source={dbPath};Mode=ReadOnly");
            recheck.Open();
            using SqliteCommand recount = recheck.CreateCommand();
            recount.CommandText = "SELECT count(*) FROM chat";
            long afterSecond = (long)(recount.ExecuteScalar() ?? 0L);

            Assert.Equal(2L, afterFirst);
            Assert.Equal(afterFirst, afterSecond);

            using SqliteCommand ftsCount = recheck.CreateCommand();
            ftsCount.CommandText = "SELECT count(*) FROM chat_fts";
            Assert.Equal(2L, (long)(ftsCount.ExecuteScalar() ?? 0L));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            File.Delete(transcript);
        }
    }

    [Fact]
    public void ScrubsTokenShapedStringsFromStoredChatRowsInBothTheOldAndNewCode()
    {
        // RESTRUCTURE.md design checklist "Secrets in outputs": index-chat removes token-shaped
        // strings (JWTs, Bearer headers, common key prefixes, PEM private keys) before it stores chat.
        // Checked against BOTH today's aitm.cs IndexChat (the oracle, the one allowed aitm.cs change
        // for this step) and the new IndexChatTool.
        string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        string bearer = "Bearer abcDEF123456.ghIJKL7890-secretvalue";
        string ghp = "ghp_1234567890abcdefghijklmnopqrstuvwx";
        string awsKey = "AKIAIOSFODNN7EXAMPLE";
        string pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAK8example\n-----END RSA PRIVATE KEY-----";

        string oldInstance = AitmCliRunner.NewTestInstance("index-chat-secrets-old");
        string newInstance = AitmCliRunner.NewTestInstance("index-chat-secrets-new");
        string transcript = MakeSecretsFixtureTranscript("index-chat-secrets-session", jwt, bearer, ghp, awsKey, pem);
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            AitmCliRunner.Run($"index-chat --instance {oldInstance} --from \"{transcript}\"");
            string oldDbPath = AitmCliRunner.InstanceDbPath(oldInstance);
            string oldStoredText = ReadAllChatText(oldDbPath);

            AitmCliRunner.Run($"init --instance {newInstance}");
            string newDbPath = AitmCliRunner.InstanceDbPath(newInstance);
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
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
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
        string instance = AitmCliRunner.NewTestInstance("index-chat-no-false-positive");
        string transcript = MakeFixtureTranscriptWithMessage("index-chat-no-fp-session", plainMessage);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
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
            AitmCliRunner.DeleteInstance(instance);
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
        string path = Path.Combine(Path.GetTempPath(), $"aitm-{session}-{Guid.NewGuid():N}.jsonl");
        (string uuid, string content)[] messages =
        [
            ("66666666-6666-6666-6666-666666666666", $"here is a token {jwt} sent by mistake, well over the forty character gate"),
            ("77777777-7777-7777-7777-777777777777", $"the header was {bearer} in the failing request, also well over the gate"),
            ("88888888-8888-8888-8888-888888888888", $"leaked github token {ghp} and aws key {awsKey} in the same paste"),
            ("99999999-9999-9999-9999-999999999999", $"pasted a pem block by accident: {pem} that should never land here"),
        ];
        string[] lines = messages.Select((m, i) => System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "user",
            uuid = m.uuid,
            timestamp = $"2026-09-25T11:0{i}:00.000Z",
            message = new { content = m.content },
        })).ToArray();
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string MakeFixtureTranscriptWithMessage(string session, string message)
    {
        string path = Path.Combine(Path.GetTempPath(), $"aitm-{session}-{Guid.NewGuid():N}.jsonl");
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
        string instance = AitmCliRunner.NewTestInstance("index-chat-missing");
        string missingFile = Path.Combine(Path.GetTempPath(), $"aitm-index-chat-missing-{Guid.NewGuid():N}.jsonl");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            (string stdout, int exitCode) = AitmCliRunner.Run($"index-chat --instance {instance} --from \"{missingFile}\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            string actual = new IndexChatTool().Execute(connection, missingFile).Trim();

            Assert.Equal(expected, actual);
            Assert.Contains("not found", actual);
            Assert.Contains("done: 0 user message(s) indexed", actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string MakeFixtureTranscript(string session)
    {
        string path = Path.Combine(Path.GetTempPath(), $"aitm-{session}-{Guid.NewGuid():N}.jsonl");
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
