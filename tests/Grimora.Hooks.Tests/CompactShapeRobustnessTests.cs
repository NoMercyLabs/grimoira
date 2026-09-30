using System.Text.Json;
using Grimora.Hooks.Data;
using Grimora.Hooks.Tools;
using Grimora.TestSupport;
using Xunit;

namespace Grimora.Hooks.Tests;

/// <summary>
/// A real 3,076-line transcript crashed CompactBriefTool.Execute silently (it returns "" and writes
/// nothing) because <c>toolUseResult</c> and other fields are not always the JSON object shape the
/// classifier assumed: on the 8 largest real transcripts, <c>user.toolUseResult</c> was a string 1,167
/// times and an array 1,962 times, <c>queued_command.prompt</c> was an array (not a string) 601 times —
/// every one of them the user's own mid-turn words — and a compaction boundary is really marked by
/// <c>user.isCompactSummary == true</c>, not only by matching the legacy "This session is being
/// continued..." text. These tests pin the fixes: every shape is read without throwing, an array prompt
/// with an image block is readable text, the boundary is found from the real marker, and — when something
/// still goes wrong that these guards did not anticipate — the failure is never silent again.
/// </summary>
public class CompactShapeRobustnessTests
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
    public void ToolUseResultShapeVariantsNearAskUserQuestionNeverThrow()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                // The question.
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
                                input = new { questions = new object[] { new { question = "Which TV?", header = "TV" } } },
                            },
                        },
                    },
                    timestamp = "2026-09-30T10:00:00Z",
                }),
                // The real answer: toolUseResult.answers is an object, as the classifier expects.
                JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new { content = new object[] { new { type = "tool_result", tool_use_id = "toolu_ask1", content = "answered" } } },
                    toolUseResult = new { answers = new Dictionary<string, string> { ["Which TV?"] = "Living room." } },
                    timestamp = "2026-09-30T10:01:00Z",
                }),
                // A malformed entry referencing the same question: toolUseResult is an ARRAY, not an
                // object — the exact shape (1,962 of 49,697 real toolUseResult values) that threw
                // InvalidOperationException inside TryGetAnswers before the fix.
                JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new { content = new object[] { new { type = "tool_result", tool_use_id = "toolu_ask1", content = "answered" } } },
                    toolUseResult = new object[] { "unexpected", "array" },
                    timestamp = "2026-09-30T10:02:00Z",
                }),
                // Same again with toolUseResult as a STRING (1,167 of 49,697 real values).
                JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new { content = new object[] { new { type = "tool_result", tool_use_id = "toolu_ask1", content = "answered" } } },
                    toolUseResult = "unexpected-string",
                    timestamp = "2026-09-30T10:03:00Z",
                }),
            ]);

            string stdout = CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-shapes"));
            string ledger = File.ReadAllText(HookPaths.LedgerPath(instance, "sess-shapes"));

            Assert.False(stdout.StartsWith("GRIMORA COMPACTION LEDGER FAILED", StringComparison.Ordinal), stdout);
            Assert.Contains("Which TV?: Living room.", ledger);
            // The malformed array/string entries must not have produced a bogus second "answer" ledger
            // block (the header line itself also contains the words "User (answer)", so this counts the
            // per-entry headings, not the summary line).
            Assert.Equal(1, CountOccurrences(ledger, "## User (answer)"));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void QueuedCommandArrayPromptWithImageAndTextIsFormatted()
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
                    attachment = new
                    {
                        type = "queued_command",
                        commandMode = "prompt",
                        source_uuid = "queued-array-1",
                        prompt = new object[]
                        {
                            new { type = "image", source = new { data = "base64==" } },
                            new { type = "text", text = "Check this screenshot before you continue." },
                        },
                    },
                    uuid = "u-array-1",
                    timestamp = "2026-09-30T10:00:00Z",
                }),
            ]);

            CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-array-prompt"));
            string ledger = File.ReadAllText(HookPaths.LedgerPath(instance, "sess-array-prompt"));

            Assert.Contains("[image]\nCheck this screenshot before you continue.", ledger);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void CompactionBoundaryComesFromIsCompactSummaryMarkerNotOnlyLegacyText()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Old message before boundary." }, timestamp = "2026-09-30T09:00:00Z" }),
                JsonSerializer.Serialize(new
                {
                    type = "attachment",
                    attachment = new { type = "queued_command", prompt = "Old mid-turn before boundary.", commandMode = "prompt", source_uuid = "q-old-1" },
                    uuid = "u-old-1",
                    timestamp = "2026-09-30T09:01:00Z",
                }),
                // Marked as a compaction summary via the real field, with text that does NOT match the
                // legacy "This session is being continued..." regex — the shape the old boundary finder
                // silently missed.
                JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new { content = "Continuing with fresh context, unrelated wording." },
                    isCompactSummary = true,
                    timestamp = "2026-09-30T09:02:00Z",
                }),
                JsonSerializer.Serialize(new { type = "user", message = new { content = "New message after boundary." }, timestamp = "2026-09-30T09:03:00Z" }),
            ]);

            string stdout = CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-boundary"));

            Assert.Contains("New message after boundary.", stdout);
            Assert.DoesNotContain("Old message before boundary.", stdout);
            Assert.DoesNotContain("Old mid-turn before boundary.", stdout);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void ForcedExceptionProducesFailedLineAndErrorLog()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = Path.Combine(projectDir, "transcript.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Fix the widget." }, timestamp = "2026-09-30T10:00:00Z" }),
            ]);

            // A genuine, unanticipated failure no shape guard can cover: the transcript file is locked
            // exclusively (mid-write by Claude Code itself, or an indexer) while PreCompact tries to read
            // it. File.ReadAllLines throws IOException, uncaught anywhere in ReadEntries — exactly what the
            // top-level catch exists for.
            string stdout;
            using (new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                stdout = CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-forced-error"));
            }

            Assert.StartsWith("GRIMORA COMPACTION LEDGER FAILED:", stdout, StringComparison.Ordinal);

            string errorLogPath = Path.Combine(HookPaths.InstanceDir(instance), "compact", "sess-forced-error.error.log");
            Assert.True(File.Exists(errorLogPath), $"expected an error log at {errorLogPath}");
            string logText = File.ReadAllText(errorLogPath);
            Assert.Contains("IOException", logText);
            Assert.Contains(errorLogPath, stdout);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void PastedDocumentBlockAppearsWholeBeforeTypedText()
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
                    type = "user",
                    message = new
                    {
                        content = new object[]
                        {
                            new
                            {
                                type = "document",
                                title = "notes.txt",
                                source = new { type = "text", media_type = "text/plain", data = "Full pasted file contents here." },
                            },
                            new { type = "text", text = "Please review this." },
                        },
                    },
                    timestamp = "2026-09-30T10:00:00Z",
                }),
            ]);

            string stdout = CompactBriefTool.Execute(Payload(transcriptPath, projectDir, "sess-document"));
            string ledger = File.ReadAllText(HookPaths.LedgerPath(instance, "sess-document"));

            const string documentBlock = "[document: notes.txt]\nFull pasted file contents here.";
            Assert.Contains(documentBlock, ledger);
            Assert.Contains(documentBlock, stdout);
            Assert.True(
                ledger.IndexOf(documentBlock, StringComparison.Ordinal) < ledger.IndexOf("Please review this.", StringComparison.Ordinal),
                "the pasted document must appear before the typed text of the same message");
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
}
