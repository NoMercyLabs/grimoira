using System.Globalization;
using Grimoira.Hooks.Tools;
using Grimoira.TestSupport;
using Xunit;

namespace Grimoira.Hooks.Tests;

/// <summary>
/// The pause switch: `grimoira gates off` writes a marker and the edit gate and prompt recall stay quiet
/// until `gates on` or 12 hours, whichever comes first; every flip lands in gates.log. A marker that
/// cannot be read counts as "on", never as "off".
/// </summary>
public class GateSwitchTests
{
    [Fact]
    public void OffWritesMarkerAndLogLine()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("gates-off");
        try
        {
            string line = GateSwitch.Off(instance, "lane 4 verify");

            Assert.Equal("Grimoira gates off (edit gate, prompt recall) until `grimoira gates on` or 12 h.", line);
            Assert.True(File.Exists(GateSwitch.PausePath(instance)));
            Assert.EndsWith("|lane 4 verify", File.ReadAllText(GateSwitch.PausePath(instance)).Trim());
            Assert.Contains(" off lane 4 verify", File.ReadAllText(GateSwitch.LogPath(instance)));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void OnRemovesMarkerAndLogsOn()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("gates-on");
        try
        {
            Assert.Equal("Grimoira gates on.", GateSwitch.On(instance));

            GateSwitch.Off(instance, "x");
            string line = GateSwitch.On(instance);

            Assert.Equal("Grimoira gates on.", line);
            Assert.False(File.Exists(GateSwitch.PausePath(instance)));
            string[] log = File.ReadAllLines(GateSwitch.LogPath(instance));
            Assert.EndsWith(" on", log[^1]);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void IsPausedFalseWithoutMarker()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("gates-nomarker");
        try
        {
            Assert.False(GateSwitch.IsPaused(instance));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void IsPausedTrueWithinTwelveHours()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("gates-fresh");
        try
        {
            GateSwitch.Off(instance, "fresh");
            Assert.True(GateSwitch.IsPaused(instance));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void ExpiredMarkerCountsAsOnAndIsRemoved()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("gates-expired");
        try
        {
            string marker = GateSwitch.PausePath(instance);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            string old = DateTime.UtcNow.AddHours(-13).ToString("o", CultureInfo.InvariantCulture);
            File.WriteAllText(marker, $"{old}|old reason");

            Assert.False(GateSwitch.IsPaused(instance));
            Assert.False(File.Exists(marker));
            Assert.Contains(" on (expired)", File.ReadAllText(GateSwitch.LogPath(instance)));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void StatusTextOffAndOn()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("gates-status");
        try
        {
            Assert.Equal("Grimoira gates: on", GateSwitch.Status(instance));

            GateSwitch.Off(instance, "status check");
            string off = GateSwitch.Status(instance);
            Assert.StartsWith("Grimoira gates: off since ", off);
            Assert.EndsWith(" (status check)", off);

            GateSwitch.On(instance);
            Assert.Equal("Grimoira gates: on", GateSwitch.Status(instance));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
