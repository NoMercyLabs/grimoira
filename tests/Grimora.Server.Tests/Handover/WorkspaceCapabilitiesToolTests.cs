using Grimora.Brain.Data;
using Grimora.Server.Handover;
using Xunit;

namespace Grimora.Server.Tests.Handover;

/// <summary>
/// The first test <c>workspace_capabilities</c> (mcp.cs:317) has ever had (RESTRUCTURE.md slice 23,
/// docs/index/04-callers-tests-bugs.md listed it among the tools "never called by any test"). Against a
/// fake <see cref="IProcessRunner"/> — never a real Python process.
/// </summary>
public class WorkspaceCapabilitiesToolTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> ArgsSeen = [];
        public string Stdout = "scripts/build-stamp.mjs — build watermarking\n";
        public int ExitCode = 0;

        public (string Stdout, string Stderr, int ExitCode) Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null)
        {
            ArgsSeen = [.. args];
            return (Stdout, "", ExitCode);
        }
    }

    /// <summary>A fake that never returns, like a hung Python interpreter — proves the tool bounds the
    /// wait itself rather than trusting the runner to honor a timeout it was given.</summary>
    private sealed class HangingRunner : IProcessRunner
    {
        public (string Stdout, string Stderr, int ExitCode) Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null)
        {
            Thread.Sleep(Timeout.Infinite);
            return ("", "", 0);
        }
    }

    private static string MakeRootWithScript()
    {
        string root = Path.Combine(Path.GetTempPath(), $"grimora-workspace-caps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "workspace-capabilities.py"), "");
        return root;
    }

    [Fact]
    public void RunsTheScriptThroughTheInjectedRunnerAndReturnsItsOutput()
    {
        string root = MakeRootWithScript();
        try
        {
            FakeRunner runner = new();
            string result = new WorkspaceCapabilitiesTool().Execute("build watermarking", root, runner);

            Assert.Contains("build-stamp.mjs", result);
            Assert.Contains("--limit", runner.ArgsSeen);
            Assert.Contains("build watermarking", runner.ArgsSeen);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsAnEmptyQueryWithoutTouchingTheRunner()
    {
        FakeRunner runner = new() { Stdout = "should not be called" };
        string result = new WorkspaceCapabilitiesTool().Execute("   ", "/does-not-matter", runner);

        Assert.Contains("Provide a task description", result);
        Assert.Empty(runner.ArgsSeen);
    }

    [Fact]
    public void RejectsAQueryOverOneThousandCharacters()
    {
        string result = new WorkspaceCapabilitiesTool().Execute(new string('x', 1001), "/does-not-matter", new FakeRunner());

        Assert.Contains("Provide a task description", result);
    }

    [Fact]
    public void ATimedOutRunnerReturnsTheSameTimeoutMessageShapeMcpCsGives()
    {
        // mcp.cs's RunWorkspacePython (mcp.cs:356) returns "{operation} timed out; coverage is
        // incomplete. Narrow the task and retry." on timeout. The runner here never returns; the
        // small override keeps the test fast while proving the same bound applies.
        string root = MakeRootWithScript();
        try
        {
            string result = new WorkspaceCapabilitiesTool().Execute(
                "build watermarking", root, new HangingRunner(), TimeSpan.FromMilliseconds(50));

            Assert.Contains("Workspace lookup timed out; coverage is incomplete. Narrow the task and retry.", result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
