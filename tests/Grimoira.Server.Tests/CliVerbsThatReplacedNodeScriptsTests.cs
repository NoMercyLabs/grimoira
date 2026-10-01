using Grimoira.Server.Data;
using Grimoira.TestSupport;
using Xunit;

namespace Grimoira.Server.Tests;

// index-code.mjs, hook-doctor.mjs and init.mjs are gone: the same work is now the CLI verbs
// `index-code`, `hooks-doctor` and `init --full`. These prove the verbs are reachable, not the tools
// behind them (IndexCodeToolTests, HookDoctorToolTests and InitFullTests own that).
public class CliVerbsThatReplacedNodeScriptsTests
{
    private static (int Exit, string Out, string Err) Run(string instance, string cwd, params string[] args)
    {
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = CliDispatch.Run([.. args, "--instance", instance], cwd, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void IndexCodeIsAVerbAndRunsOnAnEmptyStore()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("verb-index-code");
        try
        {
            (int exit, string output, string error) = Run(instance, Path.GetTempPath(), "index-code");
            Assert.Equal(0, exit);
            Assert.Equal("", error);
            Assert.DoesNotContain("unknown command", output, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("", output.Trim());
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void HooksDoctorIsAVerbAndPrintsTheAuditLine()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("verb-hooks-doctor");
        string project = Path.Combine(Path.GetTempPath(), "hooks-doctor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            (int exit, string output, _) = Run(instance, project, "hooks-doctor", "--project", project);
            Assert.StartsWith("Grimoira hooks: ", output);
            Assert.Contains(exit, new[] { 0, 2 });
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public void InitFullIsAVerbAndRegistersTheProjectsUnderRoot()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("verb-init-full");
        string root = Path.Combine(Path.GetTempPath(), "init-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "svc"));
        File.WriteAllText(Path.Combine(root, "svc", "package.json"), "{}");
        try
        {
            (int exit, string output, _) = Run(instance, root, "init", "--full", "--root", root, "--skip-chat");
            Assert.Equal(0, exit);
            Assert.Contains("[2] projects", output);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(root, recursive: true);
        }
    }
}
