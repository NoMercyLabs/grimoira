using Grimoira.Hooks.Data;
using Xunit;

namespace Grimoira.Hooks.Tests;

/// <summary>A hook request's session_id must never move a per-session file out of the instance compact folder.</summary>
public class HookPathsSessionIdTests
{
    public static IEnumerable<object[]> HostileIds =>
    [
        ["..\\x"],
        ["../../outside"],
        ["C:\\x"],
        ["C:/x"],
        ["/etc/x"],
        ["..\\..\\..\\windows\\system32\\x"],
    ];

    private static void AssertInsideCompact(string instance, string path)
    {
        string compact = Path.GetFullPath(Path.Combine(HookPaths.InstanceDir(instance), "compact"));
        string full = Path.GetFullPath(path);
        Assert.Equal(compact, Path.GetDirectoryName(full));
    }

    [Theory]
    [MemberData(nameof(HostileIds))]
    public void BriefPathStaysInCompactFolder(string sessionId) =>
        AssertInsideCompact("inst-a", HookPaths.BriefPath("inst-a", sessionId));

    [Theory]
    [MemberData(nameof(HostileIds))]
    public void LedgerPathStaysInCompactFolder(string sessionId) =>
        AssertInsideCompact("inst-a", HookPaths.LedgerPath("inst-a", sessionId));

    [Theory]
    [MemberData(nameof(HostileIds))]
    public void GateSeenPathStaysInCompactFolder(string sessionId) =>
        AssertInsideCompact("inst-a", HookPaths.GateSeenPath("inst-a", sessionId));

    [Theory]
    [MemberData(nameof(HostileIds))]
    public void SessionFileStaysInCompactFolder(string sessionId) =>
        AssertInsideCompact("inst-a", HookPaths.SessionFile("inst-a", sessionId, ".error.log"));

    [Fact]
    public void OrdinaryIdKeepsItsName()
    {
        const string id = "3f2a9c1e-7b44-4d0a-9a51-0c1d2e3f4a5b";
        Assert.Equal(id + ".md", Path.GetFileName(HookPaths.BriefPath("inst-a", id)));
    }

    [Fact]
    public void EmptyAndAllStrippedIdsFallBackToX()
    {
        Assert.Equal("x.md", Path.GetFileName(HookPaths.BriefPath("inst-a", null)));
        Assert.Equal("x.md", Path.GetFileName(HookPaths.BriefPath("inst-a", "..")));
    }
}
