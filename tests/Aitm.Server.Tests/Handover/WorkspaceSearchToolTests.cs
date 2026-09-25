using Aitm.Brain.Data;
using Aitm.Server.Handover;
using Xunit;

namespace Aitm.Server.Tests.Handover;

/// <summary>
/// mcp.cs's <c>workspace_search</c> (mcp.cs:327) against a fake <see cref="IProcessRunner"/> — never a
/// real Python process. Ported coverage: workspace-tools.test.mjs proved the tool was registered and did
/// not stall the child's stdin; this proves the tool's own validation and argument building, which the
/// Node test never exercised.
/// </summary>
public class WorkspaceSearchToolTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public string FileNameSeen = "";
        public List<string> ArgsSeen = [];
        public string Stdout = "src/one.py:1\n";
        public int ExitCode = 0;

        public (string Stdout, string Stderr, int ExitCode) Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null)
        {
            FileNameSeen = fileName;
            ArgsSeen = [.. args];
            return (Stdout, "", ExitCode);
        }
    }

    /// <summary>A fake that never returns — proves the tool bounds the wait itself.</summary>
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
        string root = Path.Combine(Path.GetTempPath(), $"aitm-workspace-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "workspace-search.py"), "");
        return root;
    }

    [Fact]
    public void RunsTheScriptThroughTheInjectedRunnerAndReturnsItsOutput()
    {
        string root = MakeRootWithScript();
        try
        {
            FakeRunner runner = new();
            string result = new WorkspaceSearchTool().Execute("nomercy-app-web", "needle", "", false, root, runner);

            Assert.Contains("src/one.py:1", result);
            Assert.Contains("--repo", runner.ArgsSeen);
            Assert.Contains("nomercy-app-web", runner.ArgsSeen);
            Assert.Contains("--pattern", runner.ArgsSeen);
            Assert.Contains("needle", runner.ArgsSeen);
            Assert.DoesNotContain("--names", runner.ArgsSeen);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NamesAndPathAreForwardedWhenGiven()
    {
        string root = MakeRootWithScript();
        try
        {
            FakeRunner runner = new();
            new WorkspaceSearchTool().Execute("nomercy-app-web", "needle", "src/lib", true, root, runner);

            Assert.Contains("--names", runner.ArgsSeen);
            Assert.Contains("--path", runner.ArgsSeen);
            Assert.Contains("src/lib", runner.ArgsSeen);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsAMissingRepositoryWithoutTouchingTheRunner()
    {
        FakeRunner runner = new();
        string result = new WorkspaceSearchTool().Execute("", "needle", "", false, "/does-not-matter", runner);

        Assert.Contains("Provide a registered repository", result);
        Assert.Equal("", runner.FileNameSeen);
    }

    [Fact]
    public void ReportsIncompleteWhenTheScriptExitsNonZero()
    {
        string root = MakeRootWithScript();
        try
        {
            FakeRunner runner = new() { ExitCode = 2, Stdout = "" };
            string result = new WorkspaceSearchTool().Execute("nomercy-app-web", "needle", "", false, root, runner);

            Assert.Contains("incomplete (exit 2)", result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ATimedOutRunnerReturnsTheSameTimeoutMessageShapeMcpCsGives()
    {
        // mcp.cs's RunWorkspacePython (mcp.cs:356) returns "{operation} timed out; coverage is
        // incomplete. Narrow the task and retry." on timeout.
        string root = MakeRootWithScript();
        try
        {
            string result = new WorkspaceSearchTool().Execute(
                "nomercy-app-web", "needle", "", false, root, new HangingRunner(), TimeSpan.FromMilliseconds(50));

            Assert.Contains("Workspace search timed out; coverage is incomplete. Narrow the task and retry.", result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
