using Aitm.Brain.Data;
using Aitm.Cli.Tools;
using Xunit;

namespace Aitm.Cli.Tests;

/// <summary>
/// RESTRUCTURE.md slice 27 ("Start the server at logon"): `aitm server install-logon &lt;exePath&gt;`
/// and `aitm server uninstall-logon` manage a Windows Task Scheduler entry that starts Aitm.Server as
/// the current user at logon, unelevated, after a short delay, with a bounded restart-on-failure
/// policy. The task definition is built by a pure function; installing and uninstalling run `schtasks`
/// only through the injected <see cref="IProcessRunner"/> seam (Aitm.Brain.Data.IProcessRunner, linked
/// into Aitm.Cli because Aitm.Cli may reference nothing in AITM —
/// Aitm.Layout.Tests.ReferenceDirectionTests.CliReferencesNothingInAitm). Real `schtasks` is never
/// invoked by these tests.
/// </summary>
public class ServerLogonCommandTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Args)> Calls = [];
        public int ExitCode;
        public string Stderr = "";

        public (string Stdout, string Stderr, int ExitCode) Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null)
        {
            Calls.Add((fileName, args));
            return ("", Stderr, ExitCode);
        }
    }

    private const string ExePath = @"C:\Program Files\Aitm\Aitm.Server.exe";
    private const string UserName = @"NOMERCY\owner";

    [Fact]
    public void DefinitionRunsOnlyWhenLoggedOnAsTheCurrentUserUnelevatedWithADelayAndBoundedRestarts()
    {
        WindowsLogonTaskDefinition def = WindowsLogonTask.BuildDefinition(ExePath, UserName);

        Assert.Equal("Aitm.Server", def.TaskName);
        Assert.Equal(UserName, def.UserName);
        Assert.Equal(TimeSpan.FromSeconds(30), def.Delay);
        Assert.False(def.RunElevated);
        Assert.Equal(3, def.RestartCount);
        Assert.Equal(TimeSpan.FromMinutes(1), def.RestartInterval);
        Assert.Equal(ExePath, def.ExecutablePath);
        Assert.Equal(@"C:\Program Files\Aitm", def.WorkingDirectory);
    }

    [Fact]
    public void UninstallCallsSchtasksDeleteWithForce()
    {
        FakeRunner runner = new();

        int exitCode = ServerLogonCommand.Uninstall(runner);

        Assert.Equal(0, exitCode);
        Assert.Single(runner.Calls);
        Assert.Equal("schtasks", runner.Calls[0].FileName);
        Assert.Equal(new[] { "/Delete", "/TN", "Aitm.Server", "/F" }, runner.Calls[0].Args);
    }

    [Fact]
    public void UninstallWhenTheTaskDoesNotExistStillSucceeds()
    {
        FakeRunner runner = new() { ExitCode = 1, Stderr = "ERROR: The system cannot find the file specified." };

        int exitCode = ServerLogonCommand.Uninstall(runner);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void UninstallFailureForAnyOtherReasonReturnsAClearErrorAndNonZeroExit()
    {
        FakeRunner runner = new() { ExitCode = 1, Stderr = "ERROR: Access is denied." };

        int exitCode = ServerLogonCommand.Uninstall(runner, out string error);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Access is denied", error);
    }
}
