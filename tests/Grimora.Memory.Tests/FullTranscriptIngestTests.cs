using System.Text.Json;
using Grimora.Memory.Tools;
using Grimora.Memory.Schema;
using Grimora.Store.Data;
using Grimora.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Memory.Tests;

public sealed class FullTranscriptIngestTests
{
    [Fact]
    public void StoresBothSidesWithKindsAndKeepsRepeatIngestIdempotent()
    {
        string root = Path.Combine(Path.GetTempPath(), "grimora-full-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string transcript = Path.Combine(root, "session-one.jsonl");
        string dbPath = Path.Combine(root, "store.db");
        File.WriteAllLines(transcript,
        [
            Line("user", "u1", "2026-09-28T00:00:00Z", "/goal ship the complete history"),
            Line("assistant", "a1", "2026-09-28T00:00:01Z", "I will check the full history."),
            Line("user", "u2", "2026-09-28T00:00:02Z", "This session is being continued from a previous conversation."),
            Line("user", "u3", "2026-09-28T00:00:03Z", "<command-name>/goal</command-name><command-args>ship it</command-args>"),
        ]);
        try
        {
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunner.Apply(connection, [new MemorySchema()]);
            new IndexChatTool().Execute(connection, transcript);
            new IndexChatTool().Execute(connection, transcript);
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT role,kind FROM chat ORDER BY ts";
            using SqliteDataReader reader = cmd.ExecuteReader();
            List<(string Role, string Kind)> rows = [];
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
            Assert.Equal([("user", "slash_command"), ("assistant", "assistant"), ("user", "compaction_summary"),
                ("user", "slash_command")], rows);
            reader.Close();
            cmd.CommandText = "SELECT count(*) FROM chat_fts";
            Assert.Equal(4L, cmd.ExecuteScalar());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SavesReadResultForMissingUntrackedFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "grimora-read-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string missing = Path.Combine(root, "friends message.md");
        string transcript = Path.Combine(root, "session-two.jsonl");
        File.WriteAllLines(transcript,
        [
            JsonSerializer.Serialize(new { type = "assistant", uuid = "a1", timestamp = "2026-09-28T00:00:00Z", message = new
            {
                role = "assistant", content = new[] { new { type = "tool_use", id = "tool-1", name = "Read", input = new { file_path = missing } } },
            }}),
            JsonSerializer.Serialize(new { type = "user", uuid = "u1", timestamp = "2026-09-28T00:00:01Z", message = new
            {
                role = "user", content = new[] { new { type = "tool_result", tool_use_id = "tool-1", content = "line one\nline two" } },
            }}),
        ]);
        try
        {
            using SqliteConnection connection = StoreConnection.Open(Path.Combine(root, "store.db"));
            SchemaRunner.Apply(connection, [new MemorySchema()]);
            new IndexChatTool().Execute(connection, transcript);
            using SqliteCommand select = connection.CreateCommand();
            select.CommandText = "SELECT source_path,text FROM chat WHERE kind='tool_result_doc'";
            using SqliteDataReader reader = select.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(missing, reader.GetString(0));
            Assert.Equal("line one\nline two", reader.GetString(1));
            Assert.False(reader.Read());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeduplicatesAnIdenticalTurnEvenWhenItHasAnotherUuid()
    {
        string root = Path.Combine(Path.GetTempPath(), "grimora-dedupe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string transcript = Path.Combine(root, "session-three.jsonl");
        File.WriteAllLines(transcript,
        [
            Line("user", "u1", "2026-09-28T00:00:00Z", "same owner message"),
            Line("user", "u2", "2026-09-28T00:00:00Z", "same owner message"),
        ]);
        try
        {
            using SqliteConnection connection = StoreConnection.Open(Path.Combine(root, "store.db"));
            SchemaRunner.Apply(connection, [new MemorySchema()]);
            new IndexChatTool().Execute(connection, transcript);
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM chat";
            Assert.Equal(1L, count.ExecuteScalar());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PastedGoalRemainsOwnerInputAndItsContentIsRecoverable()
    {
        string root = Path.Combine(Path.GetTempPath(), "grimora-pasted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string transcript = Path.Combine(root, "session-four.jsonl");
        File.WriteAllLines(transcript,
        [Line("user", "u1", "2026-09-28T00:00:00Z", "<pasted_content id=\"1\">\n/goal Monorepo Maintainance\nDetails here\n</pasted_content id=\"1\">")]);
        try
        {
            using SqliteConnection connection = StoreConnection.Open(Path.Combine(root, "store.db"));
            SchemaRunner.Apply(connection, [new MemorySchema()]);
            new IndexChatTool().Execute(connection, transcript);
            using SqliteCommand kinds = connection.CreateCommand();
            kinds.CommandText = "SELECT group_concat(kind, ',') FROM chat ORDER BY kind";
            string result = (string)kinds.ExecuteScalar()!;
            Assert.Contains("slash_command", result);
            Assert.Contains("tool_result_doc", result);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static string Line(string type, string uuid, string timestamp, string content) =>
        JsonSerializer.Serialize(new { type, uuid, timestamp, message = new { role = type, content } });
}
