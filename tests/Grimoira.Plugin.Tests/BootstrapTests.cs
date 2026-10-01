using System.Diagnostics;
using Xunit;

namespace Grimoira.Plugin.Tests;

// bootstrap.cs is the SessionStart step of the installed plugin. These tests run it as a process on a temp
// plugin folder, the way the .mjs tests ran session-start.mjs: the behaviour pinned there, pinned here.
public class BootstrapTests
{
    private const string BuildingLine = "Grimoira is building its CLI and server in the background";

    [Fact]
    public void AFreshDataFolderStartsOneBuildPrintsOneLineAndExitsZeroWithoutRunningTheHook()
    {
        using PluginFixture f = new();
        (int exit, string output, _) = f.Run();
        Assert.Equal(0, exit);
        Assert.StartsWith(BuildingLine, output);
        Assert.DoesNotContain('\n', output);
        Assert.DoesNotContain("hook SessionStart", output);
        f.WaitForBuild();
        Assert.NotNull(f.CurrentTarget());
        Assert.Single(f.Builds());
    }

    // Found by the end-to-end run of the old step: with no data folder yet the lock file could not be created,
    // which read as "another session is building", so nothing ever built.
    [Fact]
    public void ADataFolderThatDoesNotExistYetIsCreatedAndTheBuildStarts()
    {
        using PluginFixture f = new(withData: false);
        (int exit, string output, _) = f.Run();
        Assert.Equal(0, exit);
        Assert.StartsWith(BuildingLine, output);
        f.WaitForBuild();
        Assert.NotNull(f.CurrentTarget());
    }

    [Fact]
    public void ASecondSessionStartWhileTheFirstBuildRunsStartsNoSecondBuild()
    {
        using PluginFixture f = new();
        string lockFile = Path.Combine(f.Data, "build.lock");
        File.WriteAllText(lockFile, "held by another session");
        (int exit, string output, _) = f.Run();
        Assert.Equal(0, exit);
        Assert.StartsWith(BuildingLine, output);
        Thread.Sleep(3000);
        Assert.Equal("held by another session", File.ReadAllText(lockFile));
        Assert.Empty(f.Builds());
    }

    [Fact]
    public void ALockOlderThanThirtyMinutesWasLeftByADeadBuildSoTheBuildStarts()
    {
        using PluginFixture f = new();
        string lockFile = Path.Combine(f.Data, "build.lock");
        File.WriteAllText(lockFile, "dead");
        File.SetLastWriteTimeUtc(lockFile, DateTime.UtcNow.AddMinutes(-31));
        f.Run();
        f.WaitForBuild();
        Assert.NotNull(f.CurrentTarget());
    }

    [Fact]
    public void AFinishedBuildIsCurrentNoBuildStartsAndTheHookRunsThroughCurrentWithItsExitCode()
    {
        using PluginFixture f = new();
        f.Run();
        f.WaitForBuild();
        (int exit, string output, _) = f.Run();
        Assert.Equal(3, exit);
        Assert.Contains("hook SessionStart", output);
        Assert.Contains("ROOT=" + f.Root, output); // the hook gets the plugin root
        Assert.False(File.Exists(Path.Combine(f.Data, "build.lock")));
        Assert.Single(f.Builds());
    }

    // A server started without the hook (the logon task, a thin client of another slot) has no GRIMOIRA_PLUGIN_ROOT,
    // so every SessionStart also records the plugin root in the data folder, where the server finds it.
    [Fact]
    public void EverySessionStartRecordsThePluginRootInTheDataFolderBuildingOrNot()
    {
        using PluginFixture f = new();
        string rootFile = Path.Combine(f.Data, "plugin-root.txt");
        f.Run();
        Assert.Equal(f.Root, File.ReadAllText(rootFile));
        f.WaitForBuild();
        File.WriteAllText(rootFile, "an old plugin version folder");
        f.Run();
        Assert.Equal(f.Root, File.ReadAllText(rootFile));
        Assert.Single(Directory.GetFiles(f.Data, "plugin-root*")); // no temp file of the atomic write is left behind
    }

    [Fact]
    public void ACheckoutRunWritesNoPluginRootFileAndTakesNoLockWhenItsBuildExists()
    {
        using PluginFixture f = new();
        Directory.CreateDirectory(Path.Combine(f.Root, "bin-cli"));
        Directory.CreateDirectory(Path.Combine(f.Root, "bin-server"));
        File.WriteAllText(Path.Combine(f.Root, "bin-cli", "grimoira.dll"), "built by build-cli.ps1");
        File.WriteAllText(Path.Combine(f.Root, "bin-server", "Grimoira.Server.dll"), "built by build-server.ps1");
        (_, string output, _) = f.Run(pluginData: false);
        Assert.DoesNotContain(BuildingLine, output);
        Assert.False(File.Exists(Path.Combine(f.Root, "plugin-root.txt")));
        Assert.False(File.Exists(Path.Combine(f.Root, "build.lock")));
    }

    [Fact]
    public void AChangedSourceMakesTheBuildStaleSoARebuildStartsAndCurrentMovesOnlyWhenItIsComplete()
    {
        using PluginFixture f = new();
        f.Run();
        f.WaitForBuild();
        string first = f.CurrentTarget()!;
        f.ChangeServerSource("return 1;");
        (int exit, string output, _) = f.Run();
        Assert.Equal(0, exit);
        Assert.StartsWith(BuildingLine, output);
        Assert.DoesNotContain("hook SessionStart", output);
        Assert.Equal(first, f.CurrentTarget()); // never points at a half-built folder
        f.WaitForBuild();
        Assert.NotEqual(first, f.CurrentTarget());
        Assert.Equal(64, File.ReadAllText(Path.Combine(f.Current, "bin-cli", "build-stamp.txt")).Trim().Length);
        Assert.True(Directory.Exists(first), "the previous build is the rollback and stays");
    }

    [Fact]
    public void AChangedSharedBuildFileAlsoMakesTheBuildStale()
    {
        using PluginFixture f = new();
        f.Run();
        f.WaitForBuild();
        File.WriteAllText(Path.Combine(f.Root, "Directory.Build.props"), "<Project><PropertyGroup /></Project>");
        (_, string output, _) = f.Run();
        Assert.StartsWith(BuildingLine, output);
        f.WaitForBuild();
    }

    [Fact]
    public void BuildOutputUnderSrcIsNotASourceChangeSoAPublishNeverMakesItsOwnBuildStale()
    {
        using PluginFixture f = new();
        f.Run();
        f.WaitForBuild();
        string cli = Path.Combine(f.Root, "src", "Grimoira.Cli");
        Directory.CreateDirectory(Path.Combine(cli, "obj"));
        File.WriteAllText(Path.Combine(cli, "obj", "project.assets.json"), "{}");
        Directory.CreateDirectory(Path.Combine(cli, "bin"));
        File.WriteAllText(Path.Combine(cli, "bin", "grimoira.dll"), "x");
        (int exit, string output, _) = f.Run();
        Assert.Equal(3, exit);
        Assert.Contains("hook SessionStart", output);
    }

    [Fact]
    public void AFailedPublishLeavesCurrentOnTheLastCompleteBuildFreesTheLockAndTheNextSessionTriesAgain()
    {
        using PluginFixture f = new();
        f.Run();
        f.WaitForBuild();
        string first = f.CurrentTarget()!;
        f.ChangeServerSource("this does not compile");
        f.Run();
        f.WaitForBuild();
        Assert.Equal(first, f.CurrentTarget());
        Assert.Contains("failed", File.ReadAllText(Path.Combine(f.Data, "build.log")));
        (_, string output, _) = f.Run();
        Assert.StartsWith(BuildingLine, output);
        f.WaitForBuild();
    }

    // Slice 29d: a server started with the hook's stdout/stderr pipes kept them open, and the hook runner waited
    // 380 s for end-of-stream. The build must not hold them: a parent whose pipes are read to the end returns
    // while the detached build is still running.
    [Fact]
    public void TheDetachedBuildDoesNotHoldTheSessionStartPipesOpen()
    {
        using PluginFixture f = new();
        using Process p = Process.Start(f.Start())!;
        p.StandardInput.Close();
        string output = p.StandardOutput.ReadToEnd(); // returns only when every holder of the pipe has closed it
        _ = p.StandardError.ReadToEnd();
        bool buildStillRuns = File.Exists(Path.Combine(f.Data, "build.lock"));
        p.WaitForExit();
        Assert.StartsWith(BuildingLine, output);
        Assert.True(buildStillRuns, "the pipes closed only after the build ended: the build holds them");
        f.WaitForBuild();
    }

    // Only Windows refuses to rename a folder in use; elsewhere a running process keeps its files after a delete.
    [Fact]
    public void ARebuildWhileAServerRunsFromTheOldFolderWritesOnlyTheNewFolderAndAnOlderBuildInUseIsKept()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PluginFixture f = new();
        f.Run();
        f.WaitForBuild();
        string v1 = f.CurrentTarget()!;
        string before = string.Join("|", Directory.GetFileSystemEntries(v1, "*", SearchOption.AllDirectories).Order());
        ProcessStartInfo hold = new("dotnet") { UseShellExecute = false, WorkingDirectory = Path.Combine(v1, "bin-server"), RedirectStandardOutput = true };
        hold.ArgumentList.Add(Path.Combine(v1, "bin-cli", "grimoira.dll"));
        hold.ArgumentList.Add("sleep");
        using Process holder = Process.Start(hold)!;
        try
        {
            Thread.Sleep(1000);
            f.ChangeServerSource("return 2;");
            f.Run();
            f.WaitForBuild();
            string v2 = f.CurrentTarget()!;
            Assert.NotEqual(v1, v2);
            Assert.Equal(before, string.Join("|", Directory.GetFileSystemEntries(v1, "*", SearchOption.AllDirectories).Order()));
            f.ChangeServerSource("return 3;");
            f.Run();
            f.WaitForBuild();
            Assert.True(Directory.Exists(v2), "the previous build is the rollback and stays");
            Assert.True(Directory.Exists(v1), "an older build in use was deleted");
        }
        finally
        {
            holder.Kill(true);
            holder.WaitForExit();
        }

        f.ChangeServerSource("return 4;");
        f.Run();
        f.WaitForBuild();
        Assert.False(Directory.Exists(v1), "an older build nothing holds is deleted");
        Assert.Equal(2, f.Builds().Length);
    }
}
