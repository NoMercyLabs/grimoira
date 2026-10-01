using Grimoira.Cli.Tools;
using Xunit;

namespace Grimoira.Cli.Tests;

/// <summary>
/// RESTRUCTURE.md slice 27: "Also write the macOS launchd and Linux systemd-user equivalents as pure
/// builders with tests, but only Windows gets the run path now." No install/uninstall command exists
/// for these yet — only the builder functions, so the shape is proven ahead of the macOS/Linux cards.
/// </summary>
public class LaunchdAndSystemdLogonTests
{
    private static WindowsLogonTaskDefinition Definition() =>
        WindowsLogonTask.BuildDefinition("/usr/local/grimoira/Grimoira.Server", "dev");

    [Fact]
    public void LaunchdPlistRunsAtLoadAfterADelayAndRestartsOnFailureWithoutStopping()
    {
        string plist = LaunchdLogonAgent.BuildPlist(Definition());

        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.Contains("<true/>", plist);
        Assert.Contains("sleep 30", plist);
        Assert.Contains("/usr/local/grimoira/Grimoira.Server", plist);
        Assert.Contains("<key>KeepAlive</key>", plist);
        Assert.Contains("<key>SuccessfulExit</key>", plist);
        Assert.Contains("<false/>", plist);
        Assert.DoesNotContain("StartInterval", plist);
    }

    [Fact]
    public void LaunchdPlistUsesTheGrimoiraLabelAndCleanupRemovesBothLabels()
    {
        string plist = LaunchdLogonAgent.BuildPlist(Definition());

        Assert.Contains("<string>tv.nomercy.grimoira.server</string>", plist);
        Assert.DoesNotContain("tv.nomercy.grimora.server", plist);
        Assert.Equal(["tv.nomercy.grimoira.server", "tv.nomercy.grimora.server"], LaunchdLogonAgent.AllLabels);
    }

    [Fact]
    public void SystemdUserUnitDelaysStartsAsTheUserAndRestartsOnFailureWithNoBurstLimit()
    {
        string unit = SystemdUserLogonUnit.Build(Definition());

        Assert.Contains("ExecStartPre=/bin/sleep 30", unit);
        Assert.Contains("ExecStart=/usr/local/grimoira/Grimoira.Server", unit);
        Assert.Contains("Restart=on-failure", unit);
        Assert.Contains("StartLimitIntervalSec=0", unit);
        Assert.Contains("WantedBy=default.target", unit);
    }
}
