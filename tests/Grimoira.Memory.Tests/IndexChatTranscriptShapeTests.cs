using System.Text.Json;
using Grimoira.Memory.Schema;
using Grimoira.Memory.Tools;
using Grimoira.Store.Data;
using Grimoira.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Memory.Tests;

/// <summary>
/// The SessionEnd chat index is the durable copy of what was said. It has to keep every shape the
/// owner's words arrive in — an AskUserQuestion answer (recorded in <c>toolUseResult.answers</c>), a
/// mid-turn <c>queued_command</c> whose prompt is a string or an array of blocks, and a pasted document
/// block — and it must never throw on a transcript line whose field is a string, an array or null where
/// an object was expected. Every fixture here is invented text, never a real transcript.
/// </summary>
public sealed class IndexChatTranscriptShapeTests
{
    [Fact]
    public void IndexesAskUserQuestionAnswersAndNeverThrowsOnToolUseResultArrayOrString()
    {
        using Fixture f = new(
        [
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                uuid = "a-ask-1",
                timestamp = "2026-09-30T10:00:00Z",
                message = new
                {
                    id = "msg-ask-1",
                    content = new object[]
                    {
                        new
                        {
                            type = "tool_use",
                            id = "toolu_fixture_ask",
                            name = "AskUserQuestion",
                            input = new { questions = new object[] { new { question = "Which colour from the fixture?", header = "Colour" } } },
                        },
                    },
                },
            }),
            // The real answer: toolUseResult is an object holding the answers map.
            JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = "u-answer-1",
                timestamp = "2026-09-30T10:01:00Z",
                message = new { content = new object[] { new { type = "tool_result", tool_use_id = "toolu_fixture_ask", content = "answered" } } },
                toolUseResult = new { answers = new Dictionary<string, string> { ["Which colour from the fixture?"] = "Blue from the fixture." } },
            }),
            // toolUseResult as an ARRAY on an entry referencing the same question: must not throw.
            JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = "u-answer-2",
                timestamp = "2026-09-30T10:02:00Z",
                message = new { content = new object[] { new { type = "tool_result", tool_use_id = "toolu_fixture_ask", content = "answered" } } },
                toolUseResult = new object[] { "unexpected", "array" },
            }),
            // toolUseResult as a STRING: must not throw.
            JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = "u-answer-3",
                timestamp = "2026-09-30T10:03:00Z",
                message = new { content = new object[] { new { type = "tool_result", tool_use_id = "toolu_fixture_ask", content = "answered" } } },
                toolUseResult = "unexpected-string",
            }),
        ]);

        string output = f.Index();

        Assert.StartsWith("  ", output, StringComparison.Ordinal);
        List<(string Role, string Kind, string Text)> rows = f.Rows("Blue from the fixture.");
        Assert.Single(rows);
        Assert.Equal("user", rows[0].Role);
        Assert.Equal("human", rows[0].Kind);
        Assert.Contains("Which colour from the fixture?: Blue from the fixture.", rows[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexesQueuedCommandPromptAsStringAndAsBlockArray()
    {
        using Fixture f = new(
        [
            JsonSerializer.Serialize(new
            {
                type = "attachment",
                uuid = "u-queued-string",
                timestamp = "2026-09-30T10:00:00Z",
                attachment = new { type = "queued_command", commandMode = "prompt", source_uuid = "q-fixture-1", prompt = "Mid-turn string prompt from the fixture." },
            }),
            JsonSerializer.Serialize(new
            {
                type = "attachment",
                uuid = "u-queued-array",
                timestamp = "2026-09-30T10:01:00Z",
                attachment = new
                {
                    type = "queued_command",
                    commandMode = "prompt",
                    source_uuid = "q-fixture-2",
                    prompt = new object[]
                    {
                        new { type = "image", source = new { data = "base64==" } },
                        new { type = "text", text = "Mid-turn array prompt from the fixture." },
                    },
                },
            }),
            // A peer's queued message is kept too, but not as the owner's own words.
            JsonSerializer.Serialize(new
            {
                type = "attachment",
                uuid = "u-queued-peer",
                timestamp = "2026-09-30T10:02:00Z",
                attachment = new
                {
                    type = "queued_command",
                    commandMode = "prompt",
                    source_uuid = "q-fixture-3",
                    origin = new { kind = "peer", name = "other-session" },
                    prompt = "Peer message from the fixture.",
                },
            }),
        ]);

        f.Index();

        (string Role, string Kind, string Text) asString = Assert.Single(f.Rows("Mid-turn string prompt from the fixture."));
        Assert.Equal(("user", "human"), (asString.Role, asString.Kind));
        (string Role, string Kind, string Text) asArray = Assert.Single(f.Rows("Mid-turn array prompt from the fixture."));
        Assert.Equal(("user", "human"), (asArray.Role, asArray.Kind));
        Assert.Contains("[image]\nMid-turn array prompt from the fixture.", asArray.Text, StringComparison.Ordinal);
        (string Role, string Kind, string Text) peer = Assert.Single(f.Rows("Peer message from the fixture."));
        Assert.Equal("agent_report", peer.Kind);
    }

    [Fact]
    public void IndexesDocumentBlockContentsAheadOfTheTypedText()
    {
        using Fixture f = new(
        [
            JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = "u-doc-1",
                timestamp = "2026-09-30T10:00:00Z",
                message = new
                {
                    content = new object[]
                    {
                        new { type = "document", title = "notes.txt", source = new { type = "text", media_type = "text/plain", data = "hello from the fixture document" } },
                        new { type = "text", text = "Please read the fixture document above." },
                    },
                },
            }),
        ]);

        f.Index();

        (string Role, string Kind, string Text) row = Assert.Single(f.Rows("hello from the fixture document"));
        Assert.Equal(("user", "human"), (row.Role, row.Kind));
        Assert.Contains("[document: notes.txt]\nhello from the fixture document", row.Text, StringComparison.Ordinal);
        Assert.True(
            row.Text.IndexOf("hello from the fixture document", StringComparison.Ordinal) < row.Text.IndexOf("Please read the fixture document above.", StringComparison.Ordinal),
            "the document must come before the typed text");
    }

    [Fact]
    public void MultipleTextBlocksKeepTheirLineBreakInTheStoredText()
    {
        using Fixture f = new(
        [
            JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = "u-two-blocks",
                timestamp = "2026-09-30T10:00:00Z",
                message = new { content = new object[] { new { type = "text", text = "First fixture line." }, new { type = "text", text = "Second fixture line." } } },
            }),
        ]);

        f.Index();

        (string Role, string Kind, string Text) row = Assert.Single(f.Rows("First fixture line."));
        Assert.Equal("First fixture line.\nSecond fixture line.", row.Text);
    }

    [Fact]
    public void ADocumentPastedWithASlashCommandIsStillASlashCommand()
    {
        using Fixture f = new(
        [
            JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = "u-doc-goal",
                timestamp = "2026-09-30T10:00:00Z",
                message = new
                {
                    content = new object[]
                    {
                        new { type = "document", title = "brief.md", source = new { type = "text", media_type = "text/plain", data = "fixture brief body" } },
                        new { type = "text", text = "/goal ship the fixture" },
                    },
                },
            }),
        ]);

        f.Index();

        (string Role, string Kind, string Text) row = Assert.Single(f.Rows("/goal ship the fixture"));
        Assert.Equal("slash_command", row.Kind);
        Assert.StartsWith("[document: brief.md]\nfixture brief body", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NullStringOrArrayWhereAnObjectIsExpectedNeverThrows()
    {
        using Fixture f = new(
        [
            // message is null.
            """{"type":"user","uuid":"u-null-message","timestamp":"2026-09-30T10:00:00Z","message":null}""",
            // message is a string.
            """{"type":"user","uuid":"u-string-message","timestamp":"2026-09-30T10:00:01Z","message":"not an object"}""",
            // content is null.
            """{"type":"assistant","uuid":"a-null-content","timestamp":"2026-09-30T10:00:02Z","message":{"content":null}}""",
            // a Read tool_use whose input is a string, not an object.
            """{"type":"assistant","uuid":"a-string-input","timestamp":"2026-09-30T10:00:03Z","message":{"content":[{"type":"tool_use","id":"toolu_read","name":"Read","input":"C:/fixture.txt"}]}}""",
            // attachment is a string.
            """{"type":"attachment","uuid":"u-string-attachment","timestamp":"2026-09-30T10:00:04Z","attachment":"queued_command"}""",
            // queued_command whose prompt is null and origin is a string.
            """{"type":"attachment","uuid":"u-null-prompt","timestamp":"2026-09-30T10:00:05Z","attachment":{"type":"queued_command","prompt":null,"origin":"peer"}}""",
            // toolUseResult null on a plain user turn.
            """{"type":"user","uuid":"u-null-tur","timestamp":"2026-09-30T10:00:06Z","message":{"content":"Plain words from the fixture."},"toolUseResult":null}""",
            // a compaction summary marked by the real field.
            """{"type":"user","uuid":"u-compact","timestamp":"2026-09-30T10:00:07Z","isCompactSummary":true,"message":{"content":"Summary text from the fixture."}}""",
            // a whole line that is not an object.
            """["not","an","object"]""",
            """"just a string"""",
        ]);

        string output = f.Index();

        Assert.Contains("done:", output, StringComparison.Ordinal);
        (string Role, string Kind, string Text) plain = Assert.Single(f.Rows("Plain words from the fixture."));
        Assert.Equal("human", plain.Kind);
        (string Role, string Kind, string Text) summary = Assert.Single(f.Rows("Summary text from the fixture."));
        Assert.Equal("compaction_summary", summary.Kind);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _transcript;
        private readonly SqliteConnection _connection;

        public Fixture(IEnumerable<string> lines)
        {
            _root = Path.Combine(Path.GetTempPath(), "grimoira-chat-shapes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _transcript = Path.Combine(_root, "fixture-session.jsonl");
            File.WriteAllLines(_transcript, lines);
            _connection = StoreConnection.Open(Path.Combine(_root, "store.db"));
            SchemaRunner.Apply(_connection, [new MemorySchema()]);
        }

        public string Index() => new IndexChatTool().Execute(_connection, _transcript);

        public List<(string Role, string Kind, string Text)> Rows(string containing)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT role,kind,text FROM chat WHERE instr(text,$needle)>0 ORDER BY ts,rowid";
            cmd.Parameters.AddWithValue("$needle", containing);
            using SqliteDataReader reader = cmd.ExecuteReader();
            List<(string Role, string Kind, string Text)> rows = [];
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return rows;
        }

        public void Dispose()
        {
            _connection.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }
}
