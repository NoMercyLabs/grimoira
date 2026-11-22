using System.Diagnostics;
using System.Text.Json;
using Grimoira.Hooks.Data;
using Grimoira.Hooks.Tools;
using Grimoira.TestSupport;
using Xunit;

namespace Grimoira.Hooks.Tests;

/// <summary>
/// AUD-9500: the brief's "Repo state at compaction" section was never written because the
/// <c>git log -1 --format=%h %s</c> call split its format into two arguments and git exited 128.
/// </summary>
public class CompactRepoStateTests
{
    private static void Git(string root, params string[] args)
    {
        ProcessStartInfo psi = new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(root);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("user.name=Test");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("user.email=test@example.invalid");
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void BriefListsTheRepoWithBranchAndHead()
    {
        // Not under the OS temp folder: the brief skips scratch paths.
        string repo = Path.Combine(AppContext.BaseDirectory, $"test-repostate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        string instance = HookPaths.ResolveInstance(repo);
        string transcript = repo + ".jsonl";
        try
        {
            Git(repo, "init", "-q", "-b", "main");
            string edited = Path.Combine(repo, "Widget.cs");
            File.WriteAllText(edited, "class Widget {}");
            Git(repo, "add", "Widget.cs");
            Git(repo, "commit", "-q", "-m", "first widget");

            File.WriteAllLines(transcript,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Fix the widget." } }),
                JsonSerializer.Serialize(new
                {
                    type = "assistant",
                    message = new { content = new object[] { new { type = "tool_use", name = "Edit", input = new { file_path = edited } } } },
                }),
            ]);

            string payload = JsonSerializer.Serialize(new { transcript_path = transcript, cwd = repo, session_id = "sess-repo" });
            string stdout = CompactBriefTool.Execute(payload);

            Assert.Contains("Repo state at compaction", stdout);
            Assert.Contains("main", stdout);
            Assert.Contains("first widget", stdout);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            File.Delete(transcript);
            foreach (string f in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(repo, recursive: true);
        }
    }
}
