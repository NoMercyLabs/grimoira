using Grimoira.Hooks.Tools;
using Grimoira.Server.Data;
using Grimoira.TestSupport;
using Xunit;

namespace Grimoira.Server.Tests;

/// <summary>
/// `grimoira gates off [reason...] | on | status`: the one command that pauses the edit gate and prompt
/// recall for a session, and turns them back on. Runs in-process through CliDispatch.Run, the same way
/// the server dispatches every verb; a bare or unknown sub-verb is usage with exit 2, like the neighbours.
/// </summary>
public class GatesVerbTests
{
    private static (int exit, string stdout, string stderr) Run(string instance, params string[] args)
    {
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = CliDispatch.Run([.. args, "--instance", instance], Directory.GetCurrentDirectory(), stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void GatesOffWritesTheMarkerAndSaysSo()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("gates-verb-off");
        try
        {
            (int exit, string stdout, string stderr) = Run(instance, "gates", "off", "lane", "4");

            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.StartsWith("Grimoira gates off (edit gate, prompt recall)", stdout);
            Assert.True(GateSwitch.IsPaused(instance));
            Assert.EndsWith("|lane 4", File.ReadAllText(GateSwitch.PausePath(instance)).Trim());
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void GatesStatusReportsOffThenOn()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("gates-verb-status");
        try
        {
            Assert.StartsWith("Grimoira gates: on", Run(instance, "gates", "status").stdout);
            Run(instance, "gates", "off", "x");
            (int exit, string stdout, _) = Run(instance, "gates", "status");

            Assert.Equal(0, exit);
            Assert.StartsWith("Grimoira gates: off since ", stdout);
            Assert.Contains("(x)", stdout);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void GatesOnRemovesTheMarker()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("gates-verb-on");
        try
        {
            Run(instance, "gates", "off", "x");
            (int exit, string stdout, string stderr) = Run(instance, "gates", "on");

            Assert.Equal(0, exit);
            Assert.Equal("", stderr);
            Assert.StartsWith("Grimoira gates on.", stdout);
            Assert.False(GateSwitch.IsPaused(instance));
            Assert.False(File.Exists(GateSwitch.PausePath(instance)));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void GatesWithoutOrWithUnknownSubVerbPrintsUsageAndExitsTwo()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("gates-verb-usage");
        try
        {
            (int exit, string stdout, string stderr) = Run(instance, "gates");
            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.Contains("usage: grimoira gates off [reason...] | on | status", stderr);

            (int exit2, _, string stderr2) = Run(instance, "gates", "sideways");
            Assert.Equal(2, exit2);
            Assert.Contains("usage: grimoira gates off [reason...] | on | status", stderr2);
            Assert.False(GateSwitch.IsPaused(instance));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
