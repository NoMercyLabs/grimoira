using System.Text.Json;
using Aitm.Hooks.Data;
using Aitm.Hooks.Tools;
using Aitm.TestSupport;
using Xunit;

namespace Aitm.Hooks.Tests;

/// <summary>
/// RESTRUCTURE.md slice 20 ("Hooks, part 1"): pins the PreCompact/UserPromptSubmit round trip.
/// Neither hook has a pinned test today (the plan says "none today"), so these are the first: a
/// PreCompact payload must write anchors to disk, and the next UserPromptSubmit must return them
/// exactly once — a second call must come back empty.
/// </summary>
public class CompactHookTests
{
    private static string NewTempProjectDir()
    {
        // The instance name is derived from the basename of "cwd" (HookPaths.ResolveInstance), so the
        // temp project directory's name IS the test instance name. Prefixed with "test-" so
        // AitmCliRunner.DeleteInstance (the shared cleanup guard) accepts removing it.
        string dir = Path.Combine(Path.GetTempPath(), $"test-hooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteTranscript(string projectDir)
    {
        string path = Path.Combine(projectDir, "transcript.jsonl");
        string editedFile = Path.Combine(projectDir, "src", "Widget.cs");
        var lines = new[]
        {
            JsonSerializer.Serialize(new
            {
                type = "user",
                message = new { content = "Fix the widget so it stops crashing on empty input." },
            }),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new
                {
                    content = new object[]
                    {
                        new { type = "tool_use", name = "Edit", input = new { file_path = editedFile } },
                    },
                },
            }),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new
                {
                    content = new object[]
                    {
                        new
                        {
                            type = "tool_use",
                            name = "TodoWrite",
                            input = new
                            {
                                todos = new object[]
                                {
                                    new { status = "pending", content = "Add a regression test" },
                                    new { status = "completed", content = "Read the bug report" },
                                },
                            },
                        },
                    },
                },
            }),
        };
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Payload(string transcriptPath, string cwd, string sessionId) =>
        JsonSerializer.Serialize(new { transcript_path = transcriptPath, cwd, session_id = sessionId });

    [Fact]
    public void PreCompactPayloadWritesAnchorsToDisk()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = WriteTranscript(projectDir);
            string payload = Payload(transcriptPath, projectDir, "sess-1");

            string stdout = CompactBriefTool.Execute(payload);

            Assert.Contains("Fix the widget so it stops crashing on empty input.", stdout);
            Assert.Contains("Add a regression test", stdout);
            Assert.DoesNotContain("Read the bug report", stdout);

            string briefPath = HookPaths.BriefPath(instance, "sess-1");
            Assert.True(File.Exists(briefPath));
            string brief = File.ReadAllText(briefPath);
            Assert.Contains("Fix the widget so it stops crashing on empty input.", brief);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void UserPromptSubmitReturnsTheBriefOnceThenEmpty()
    {
        string projectDir = NewTempProjectDir();
        string instance = HookPaths.ResolveInstance(projectDir);
        try
        {
            string transcriptPath = WriteTranscript(projectDir);
            string precompactPayload = Payload(transcriptPath, projectDir, "sess-2");
            CompactBriefTool.Execute(precompactPayload);

            string promptPayload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "sess-2" });

            string first = CompactRestoreTool.Execute(promptPayload);
            Assert.NotEqual("", first);
            using JsonDocument doc = JsonDocument.Parse(first);
            string hookEventName = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString()!;
            string additionalContext = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
            Assert.Equal("UserPromptSubmit", hookEventName);
            Assert.Contains("Fix the widget so it stops crashing on empty input.", additionalContext);
            Assert.False(File.Exists(HookPaths.BriefPath(instance, "sess-2")));

            string second = CompactRestoreTool.Execute(promptPayload);
            Assert.Equal("", second);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void PreCompactWithNoTranscriptFileFailsOpenWithNoOutput()
    {
        string projectDir = NewTempProjectDir();
        try
        {
            string payload = Payload(Path.Combine(projectDir, "missing.jsonl"), projectDir, "sess-3");
            string stdout = CompactBriefTool.Execute(payload);
            Assert.Equal("", stdout);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void UserPromptSubmitWithNoBriefFailsOpenWithNoOutput()
    {
        string projectDir = NewTempProjectDir();
        try
        {
            string payload = JsonSerializer.Serialize(new { cwd = projectDir, session_id = "sess-never-compacted" });
            string stdout = CompactRestoreTool.Execute(payload);
            Assert.Equal("", stdout);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }
}
