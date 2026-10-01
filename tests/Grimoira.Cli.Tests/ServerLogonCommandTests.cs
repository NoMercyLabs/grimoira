using Grimoira.Brain.Data;
using Grimoira.Cli.Tools;
using Xunit;

namespace Grimoira.Cli.Tests;

/// <summary>
/// RESTRUCTURE.md slice 27 ("Start the server at logon"): `grimoira server install-logon &lt;exePath&gt;`
/// and `grimoira server uninstall-logon` manage a Windows Task Scheduler entry that starts Grimoira.Server as
/// the current user at logon, unelevated, after a short delay, with a bounded restart-on-failure
/// policy. The task definition is built by a pure function; installing and uninstalling run `schtasks`
/// only through the injected <see cref="IProcessRunner"/> seam (Grimoira.Brain.Data.IProcessRunner, linked
/// into Grimoira.Cli because Grimoira.Cli may reference nothing in Grimoira —
/// Grimoira.Layout.Tests.ReferenceDirectionTests.CliReferencesNothingInGrimoira). Real `schtasks` is never
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

    private const string ExePath = @"C:\Program Files\Grimoira\Grimoira.Server.exe";
    private const string UserName = @"EXAMPLE\dev";

    [Fact]
    public void DefinitionRunsOnlyWhenLoggedOnAsTheCurrentUserUnelevatedWithADelayAndBoundedRestarts()
    {
        WindowsLogonTaskDefinition def = WindowsLogonTask.BuildDefinition(ExePath, UserName);

        Assert.Equal("Grimoira.Server", def.TaskName);
        Assert.Equal(UserName, def.UserName);
        Assert.Equal(TimeSpan.FromSeconds(30), def.Delay);
        Assert.False(def.RunElevated);
        Assert.Equal(3, def.RestartCount);
        Assert.Equal(TimeSpan.FromMinutes(1), def.RestartInterval);
        Assert.Equal(ExePath, def.ExecutablePath);
        Assert.Equal(@"C:\Program Files\Grimoira", def.WorkingDirectory);
    }

    [Fact]
    public void UninstallCallsSchtasksDeleteWithForce()
    {
        FakeRunner runner = new();

        int exitCode = ServerLogonCommand.Uninstall(runner);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, c => Assert.Equal("schtasks", c.FileName));
        Assert.Equal(new[] { "/Delete", "/TN", "Grimoira.Server", "/F" }, runner.Calls[0].Args);
        Assert.Equal(new[] { "/Delete", "/TN", "Grimora.Server", "/F" }, runner.Calls[1].Args);
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
