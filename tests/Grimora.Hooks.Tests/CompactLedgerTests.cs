using System.Text.Json;
using Grimora.Hooks.Data;
using Grimora.Hooks.Tools;
using Grimora.TestSupport;
using Xunit;

namespace Grimora.Hooks.Tests;

/// <summary>
/// PreCompact must carry the owner's own words whole, not the first 400 characters of the last 4 messages
/// (the bug CompactBriefTool.Directives() had): every typed message, every mid-turn queued message,
/// every AskUserQuestion answer, and — in the verbatim ledger it now also writes — every peer message and
/// every assistant reply too.
/// </summary>
public class CompactLedgerTests
{
    private static string NewTempProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"test-hooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Payload(string transcriptPath, string cwd, string sessionId) =>
        JsonSerializer.Serialize(new { transcript_path = transcriptPath, cwd, session_id = sessionId });

    [Fact]
    public void LongUserMessageAppearsUncutInBrief()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string longMessage = new('a', 900);
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = longMessage }, timestamp = "2026-09-30T10:00:00Z" }),
            ]);

            string stdout = CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-long"));

            Assert.Contains(longMessage, stdout);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void SixUserMessagesSinceLastCompactionAllAppear()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            string[] messages =
            [
                "First directive about the encoder queue.",
                "Second directive about the login flow.",
                "Third directive about the cast receiver.",
                "Fourth directive about the ledger itself.",
                "Fifth directive about the brief budget.",
                "Sixth directive about the restore marker.",
            ];
            List<string> lines = [];
            foreach (string m in messages)
            {
                lines.Add(JsonSerializer.Serialize(new { type = "user", message = new { content = m }, timestamp = "2026-09-30T10:00:00Z" }));
            }
            File.WriteAllLines(transcriptPath, lines);

            string stdout = CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-six"));

            foreach (string m in messages)
            {
                Assert.Contains(m, stdout);
            }
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void MidTurnAndPeerQueuedMessagesAppearInLedger()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Start the work." }, timestamp = "2026-09-30T10:00:00Z" }),
                JsonSerializer.Serialize(new
                {
                    type = "attachment",
                    attachment = new
                    {
                        type = "queued_command",
                        prompt = "Also check the timeout while you're in there.",
                        commandMode = "prompt",
                        source_uuid = "queued-1",
                    },
                    uuid = "u-1",
                    timestamp = "2026-09-30T10:01:00Z",
                }),
                JsonSerializer.Serialize(new
                {
                    type = "attachment",
                    attachment = new
                    {
                        type = "queued_command",
                        prompt = "<cross-session-message from-name=\"nomercy-a9\">Do not touch scoped-rules.json.</cross-session-message>",
                        commandMode = "prompt",
                        source_uuid = "queued-2",
                        origin = new { kind = "peer", name = "nomercy-a9" },
                    },
                    uuid = "u-2",
                    timestamp = "2026-09-30T10:02:00Z",
                }),
            ]);

            CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-midturn"));
            string ledger = File.ReadAllText(HookPaths.LedgerPath(instance, "sess-midturn"));

            Assert.Contains("Also check the timeout while you're in there.", ledger);
            Assert.Contains("Peer nomercy-a9", ledger);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void AskUserQuestionAnswerAppearsInLedgerWithItsQuestion()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new
                {
                    type = "assistant",
                    message = new
                    {
                        id = "msg-ask-1",
                        content = new object[]
                        {
                            new
                            {
                                type = "tool_use",
                                id = "toolu_ask1",
                                name = "AskUserQuestion",
                                input = new
                                {
                                    questions = new object[]
                                    {
                                        new { question = "Which TV should we test on?", header = "TV" },
                                    },
                                },
                            },
                        },
                    },
                    timestamp = "2026-09-30T10:03:00Z",
                }),
                JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new
                    {
                        content = new object[]
                        {
                            new { type = "tool_result", tool_use_id = "toolu_ask1", content = "answered" },
                        },
                    },
                    toolUseResult = new { answers = new Dictionary<string, string> { ["Which TV should we test on?"] = "The living-room TV." } },
                    timestamp = "2026-09-30T10:04:00Z",
                }),
            ]);

            CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-answer"));
            string ledger = File.ReadAllText(HookPaths.LedgerPath(instance, "sess-answer"));

            Assert.Contains("Which TV should we test on?", ledger);
            Assert.Contains("The living-room TV.", ledger);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void AssistantReplyTextAppearsInLedger()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Fix the widget." }, timestamp = "2026-09-30T10:00:00Z" }),
                JsonSerializer.Serialize(new
                {
                    type = "assistant",
                    message = new
                    {
                        id = "msg-reply-1",
                        content = new object[] { new { type = "text", text = "Found the cause: a null check missing at line 42." } },
                    },
                    timestamp = "2026-09-30T10:01:00Z",
                }),
            ]);

            CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-reply"));
            string ledger = File.ReadAllText(HookPaths.LedgerPath(instance, "sess-reply"));

            Assert.Contains("Found the cause: a null check missing at line 42.", ledger);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void RestoreKeepsBriefAndLedgerFilesOnDisk()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Fix the widget so it stops crashing." }, timestamp = "2026-09-30T10:00:00Z" }),
            ]);
            CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-restore"));

            string promptPayload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "sess-restore" });
            string first = CompactRestoreTool.Execute(promptPayload);

            Assert.NotEqual("", first);
            Assert.True(File.Exists(HookPaths.BriefPath(instance, "sess-restore")));
            Assert.True(File.Exists(HookPaths.LedgerPath(instance, "sess-restore")));

            // one-shot still holds: a second call after the same restore returns empty.
            string second = CompactRestoreTool.Execute(promptPayload);
            Assert.Equal("", second);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void ToolResultAndTaskNotificationNoiseNeverTriggersLedgerLoss()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            List<string> lines = [];
            for (int i = 0; i < 50; i++)
            {
                lines.Add(JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new { content = new object[] { new { type = "tool_result", tool_use_id = $"t{i}", content = "ok" } } },
                    timestamp = "2026-09-30T09:00:00Z",
                }));
            }
            for (int i = 0; i < 3; i++)
            {
                lines.Add(JsonSerializer.Serialize(new
                {
                    type = "attachment",
                    attachment = new { type = "queued_command", prompt = $"Background task {i} finished.", commandMode = "task-notification", source_uuid = $"tn-{i}" },
                    uuid = $"u-tn-{i}",
                    timestamp = "2026-09-30T09:01:00Z",
                }));
            }
            string[] ownerMessages = ["the owner message one.", "the owner message two.", "the owner message three."];
            foreach (string m in ownerMessages)
            {
                lines.Add(JsonSerializer.Serialize(new { type = "user", message = new { content = m }, timestamp = "2026-09-30T09:02:00Z" }));
            }
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "user",
                message = new { content = "This session is being continued from a previous conversation that ran out of context." },
                timestamp = "2026-09-30T09:03:00Z",
            }));
            File.WriteAllLines(transcriptPath, lines);

            CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-noloss"));
            string promptPayload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "sess-noloss", transcript_path = transcriptPath });
            string restore = CompactRestoreTool.Execute(promptPayload);

            Assert.DoesNotContain("LEDGER LOSS", restore);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void MissingLedgerEntryIsReportedAsLedgerLoss()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            string[] ownerMessages = ["First message.", "Second message.", "Third message."];
            List<string> lines = [];
            foreach (string m in ownerMessages)
            {
                lines.Add(JsonSerializer.Serialize(new { type = "user", message = new { content = m }, timestamp = "2026-09-30T09:02:00Z" }));
            }
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "user",
                message = new { content = "This session is being continued from a previous conversation that ran out of context." },
                timestamp = "2026-09-30T09:03:00Z",
            }));
            File.WriteAllLines(transcriptPath, lines);

            CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-loss"));

            string ledgerPath = HookPaths.LedgerPath(instance, "sess-loss");
            string ledger = File.ReadAllText(ledgerPath);
            File.WriteAllText(ledgerPath, ledger.Replace("Second message.", "REDACTED"));

            string promptPayload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "sess-loss", transcript_path = transcriptPath });
            string restore = CompactRestoreTool.Execute(promptPayload);

            Assert.Contains("LEDGER LOSS: 1 of 3", restore);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void TypedMidTurnTypedOrderIsPreservedInBrief()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Typed one." }, timestamp = "2026-09-30T10:00:00Z" }),
                JsonSerializer.Serialize(new
                {
                    type = "attachment",
                    attachment = new { type = "queued_command", prompt = "Mid turn one.", commandMode = "prompt", source_uuid = "q-order-1" },
                    uuid = "u-order-1",
                    timestamp = "2026-09-30T10:01:00Z",
                }),
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Typed two." }, timestamp = "2026-09-30T10:02:00Z" }),
            ]);

            string stdout = CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-order"));

            int i1 = stdout.IndexOf("Typed one.", StringComparison.Ordinal);
            int i2 = stdout.IndexOf("Mid turn one.", StringComparison.Ordinal);
            int i3 = stdout.IndexOf("Typed two.", StringComparison.Ordinal);
            Assert.True(i1 >= 0 && i2 >= 0 && i3 >= 0, stdout);
            Assert.True(i1 < i2, "Typed one. must appear before Mid turn one.");
            Assert.True(i2 < i3, "Mid turn one. must appear before Typed two.");
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void MultiLineMessageKeepsItsLineBreaksInBriefAndLedger()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            string multiline = "Line one.\nLine two.\nLine three.";
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = multiline }, timestamp = "2026-09-30T10:00:00Z" }),
            ]);

            string stdout = CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-multiline"));
            string ledger = File.ReadAllText(HookPaths.LedgerPath(instance, "sess-multiline"));

            Assert.Contains(multiline, stdout);
            Assert.Contains(multiline, ledger);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void DuplicateQueuedCommandBySourceUuidAppearsOnceInBriefAndLedger()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new
                {
                    type = "attachment",
                    attachment = new { type = "queued_command", prompt = "Also check the timeout.", commandMode = "prompt", source_uuid = "dup-1" },
                    uuid = "u-dup-1a",
                    timestamp = "2026-09-30T10:01:00Z",
                }),
                JsonSerializer.Serialize(new
                {
                    type = "attachment",
                    attachment = new { type = "queued_command", prompt = "Also check the timeout.", commandMode = "prompt", source_uuid = "dup-1" },
                    uuid = "u-dup-1b",
                    timestamp = "2026-09-30T10:01:05Z",
                }),
            ]);

            string stdout = CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-dup"));
            string ledger = File.ReadAllText(HookPaths.LedgerPath(instance, "sess-dup"));

            int briefCount = CountOccurrences(stdout, "Also check the timeout.");
            int ledgerCount = CountOccurrences(ledger, "Also check the timeout.");
            Assert.Equal(1, briefCount);
            Assert.Equal(1, ledgerCount);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact]
    public void LedgerGenerationIsDeterministic()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Fix the widget." }, timestamp = "2026-09-30T10:00:00Z" }),
                JsonSerializer.Serialize(new
                {
                    type = "assistant",
                    message = new { id = "msg-1", content = new object[] { new { type = "text", text = "Done." } } },
                    timestamp = "2026-09-30T10:01:00Z",
                }),
            ]);
            string payload = Payload(transcriptPath, projectDir, "sess-det");

            CompactBriefTool.Execute(payload);
            string first = File.ReadAllText(HookPaths.LedgerPath(instance, "sess-det"));
            CompactBriefTool.Execute(payload);
            string second = File.ReadAllText(HookPaths.LedgerPath(instance, "sess-det"));

            Assert.Equal(first, second);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }
}
