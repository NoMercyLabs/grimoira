using Xunit;

namespace Grimora.Plugin.Tests;

// 2026-09-30: a session started before the 1.0.0 install kept running cache/grimora/0.4.0/bootstrap.cs at every
// SessionStart (compaction too). Its tree hash never matched the 1.0.0 build `current` pointed at, so it rebuilt
// its own tree and moved `current` back to the 0.4.0 build, twice in one morning, and wrote its own root into
// plugin-root.txt. The installed root is the installPath Claude Code records in installed_plugins.json; only
// that root may move `current` or record itself.
public class InstalledRootTests
{
    private const string BuildingLine = "Grimora is building its CLI and server in the background";

    [Fact]
    public void AStaleRootLeavesCurrentAndThePluginRootFileAloneAndNamesBothRoots()
    {
        using PluginFixture f = new();
        f.WriteInstalledPlugins(f.Root);
        f.Run();
        f.WaitForBuild();
        string installedBuild = f.CurrentTarget()!;
        string rootFile = Path.Combine(f.Data, "plugin-root.txt");
        Assert.Equal(f.Root, File.ReadAllText(rootFile));
        string stale = f.AddStaleRoot("0.4.0");

        (int exit, string output, string err) = f.Run(root: stale);
        Thread.Sleep(3000); // long enough for a build that must not start to have taken the lock

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(f.Data, "build.lock")), "the stale root started a build");
        Assert.Equal(installedBuild, f.CurrentTarget());
        Assert.Single(f.Builds());
        Assert.Equal(f.Root, File.ReadAllText(rootFile));
        Assert.DoesNotContain(BuildingLine, output);
        Assert.Contains(stale, output);
        Assert.Contains(f.Root, output);
        Assert.Equal("", err);
    }

    [Fact]
    public void TheInstalledRootStillMovesCurrentWhenItsOwnBuildChanges()
    {
        using PluginFixture f = new();
        f.WriteInstalledPlugins(f.Root);
        f.Run();
        f.WaitForBuild();
        string first = f.CurrentTarget()!;
        f.ChangeServerSource("return 1;");

        (_, string output, _) = f.Run();

        Assert.StartsWith(BuildingLine, output);
        f.WaitForBuild();
        Assert.NotEqual(first, f.CurrentTarget());
        Assert.Equal(f.Root, File.ReadAllText(Path.Combine(f.Data, "plugin-root.txt")));
    }
}
