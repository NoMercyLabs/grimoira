using Xunit;

namespace Grimora.Plugin.Tests;

// `current` is the one path every hook slot and the MCP server run through. Moving it used to delete the old
// junction first and create the new one second, so a failed create (a forward-slash target from Git Bash made
// mklink /J exit 1) left no `current` at all and every hook pointed at nothing, silently. The swap now builds the
// new link beside the old one and replaces it only after it resolves; a session that finds `current` missing or
// dangling after a complete build says so in one line, with the log and the repair command.
public class CurrentSwapTests
{
    private const string BuildingLine = "Grimora is building its CLI and server in the background";

    [Fact]
    public void AFailedLinkLeavesThePreviousCurrentInPlaceAndPointingAtItsOldTarget()
    {
        using PluginFixture f = new();
        string old = Directory.CreateDirectory(Path.Combine(f.Data, "builds", "old000000000")).FullName;
        f.LinkCurrentTo(old);
        File.WriteAllText(Path.Combine(f.Data, "build.lock"), "held"); // an unknown verb must not fall through to a build
        string missing = Path.Combine(f.Data, "builds", "never-published");

        (int exit, string output, string err) = f.RunVerb("--point", f.Current, missing);

        Assert.True(exit != 0, $"a link to a missing target must fail; got exit 0 with output: {output} {err}");
        Assert.Equal(old, f.CurrentTarget());
        Assert.False(Directory.Exists(f.Current + ".next") || File.Exists(f.Current + ".next"), "the temporary link is left behind");
    }

    [Fact]
    public void ASuccessfulSwapPointsCurrentAtTheNewTargetEvenWithForwardSlashesAndLeavesNoTemporaryLink()
    {
        using PluginFixture f = new();
        string old = Directory.CreateDirectory(Path.Combine(f.Data, "builds", "old000000000")).FullName;
        string next = Directory.CreateDirectory(Path.Combine(f.Data, "builds", "new000000000")).FullName;
        f.LinkCurrentTo(old);
        File.WriteAllText(Path.Combine(f.Data, "build.lock"), "held");

        (int exit, string output, string err) = f.RunVerb("--point", f.Current.Replace('\\', '/'), next.Replace('\\', '/'));

        Assert.True(exit == 0, $"exit {exit}: {output} {err}");
        Assert.Equal(next, f.CurrentTarget());
        Assert.False(Directory.Exists(f.Current + ".next") || File.Exists(f.Current + ".next"), "the temporary link is left behind");
        Assert.True(Directory.Exists(old), "the old target is a build folder, never deleted by the swap");
    }

    // The incident itself: `bootstrap.cs --build <data>` started from Git Bash with a forward-slash data path.
    [Fact]
    public void ABuildWithAForwardSlashDataPathStillMovesCurrentToTheNewBuild()
    {
        using PluginFixture f = new();
        f.Run();
        f.WaitForBuild();
        string first = f.CurrentTarget()!;
        f.ChangeServerSource("return 7;");

        (int exit, string output, string err) = f.RunVerb("--build", f.Data.Replace('\\', '/'));

        Assert.True(exit == 0, $"exit {exit}: {output} {err}\nbuild.log:\n{File.ReadAllText(Path.Combine(f.Data, "build.log"))}");
        string? now = f.CurrentTarget();
        Assert.NotNull(now);
        Assert.NotEqual(first, now);
        Assert.False(Directory.Exists(f.Current + ".next"), "the temporary link is left behind");
    }

    [Fact]
    public void AMissingCurrentAfterACompleteBuildIsNamedWithTheLogAndTheRepairCommand()
    {
        using PluginFixture f = new();
        f.Run();
        f.WaitForBuild();
        Directory.Delete(f.Current, false); // the junction only
        File.WriteAllText(Path.Combine(f.Data, "build.lock"), "held by another session"); // so the output is only the warning

        (int exit, string output, _) = f.Run();

        Assert.Equal(0, exit);
        AssertLoud(f, output);
    }

    [Fact]
    public void ADanglingCurrentIsNamedTheSameWay()
    {
        using PluginFixture f = new();
        f.Run();
        f.WaitForBuild();
        Directory.Delete(f.CurrentTarget()!, true); // the build folder goes, the junction stays and points at nothing
        File.WriteAllText(Path.Combine(f.Data, "build.lock"), "held by another session");

        (int exit, string output, _) = f.Run();

        Assert.Equal(0, exit);
        AssertLoud(f, output);
    }

    [Fact]
    public void AFreshInstallWithNoBuildYetIsNotAWarning()
    {
        using PluginFixture f = new();
        File.WriteAllText(Path.Combine(f.Data, "build.lock"), "held by another session");
        (_, string output, _) = f.Run();
        Assert.Equal(BuildingLine, output[..Math.Min(output.Length, BuildingLine.Length)]);
        Assert.DoesNotContain("build.log", output);
    }

    private static void AssertLoud(PluginFixture f, string output)
    {
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? loud = lines.FirstOrDefault(l => l.Contains(f.Current, StringComparison.OrdinalIgnoreCase));
        Assert.True(loud is not null, $"no line names {f.Current}; output was:\n{output}");
        Assert.Contains(Path.Combine(f.Data, "build.log"), loud, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--build", loud);
        Assert.Contains("bootstrap.cs", loud);
    }
}
