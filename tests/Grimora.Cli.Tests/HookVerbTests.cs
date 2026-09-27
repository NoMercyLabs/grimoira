using System.Text.Json;
using Xunit;

namespace Grimora.Cli.Tests;

/// <summary>
/// RESTRUCTURE.md slice 20 ("Hooks, part 1"): `grimora hook &lt;event&gt;` reads the event JSON from
/// stdin and runs the matching Grimora.Hooks handler, printing whatever the handler printed (nothing, on
/// a pass-through). Grimora.Cli is the CLI entry-point project named in RESTRUCTURE.md section 1
/// ("Grimora.Cli ... Owns: the grimora command"); it is not the running host until phase 3
/// (Program.cs's own comment and RESTRUCTURE.md:531), so this exercises the verb directly rather than
/// through a built exe, the same way slices 15-19 test their moved tools directly.
/// </summary>
public class HookVerbTests
{
    private static string NewTempProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"test-cli-hook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void HookPreCompactWritesTheBriefAndPrintsIt()
    {
        string projectDir = NewTempProjectDir();
        string instance = Path.GetFileName(projectDir).ToLowerInvariant();
        try
        {
            string transcriptPath = Path.Combine(projectDir, "t.jsonl");
            File.WriteAllLines(transcriptPath,
            [
                JsonSerializer.Serialize(new { type = "user", message = new { content = "Ship the hook verb, end to end." } }),
            ]);
            string payload = JsonSerializer.Serialize(new { transcript_path = transcriptPath, cwd = projectDir, session_id = "s1" });

            (string stdout, int exitCode) = RunHookVerb("PreCompact", payload);

            Assert.Equal(0, exitCode);
            Assert.Contains("Ship the hook verb, end to end.", stdout);
        }
        finally
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string instanceDir = Path.Combine(home, ".grimora", instance);
            if (Directory.Exists(instanceDir)) Directory.Delete(instanceDir, recursive: true);
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public void HookWithAnUnknownEventFailsOpenWithNoOutputAndExitZero()
    {
        (string stdout, int exitCode) = RunHookVerb("SomeUnknownEvent", "{}");
        Assert.Equal(0, exitCode);
        Assert.Equal("", stdout);
    }

    private static (string stdout, int exitCode) RunHookVerb(string eventName, string stdinPayload)
    {
        int exitCode = RunMain(["hook", eventName], stdinPayload, out string stdout);
        return (stdout, exitCode);
    }

    private static int RunMain(string[] args, string stdin, out string stdout)
    {
        TextReader originalIn = Console.In;
        TextWriter originalOut = Console.Out;
        using StringReader reader = new(stdin);
        using StringWriter writer = new();
        try
        {
            Console.SetIn(reader);
            Console.SetOut(writer);
            int code = Program.Main(args);
            stdout = writer.ToString();
            return code;
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }
    }
}
