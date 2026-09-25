using Aitm.Brain.Data;
using Aitm.Server.Data;
using Xunit;

namespace Aitm.Server.Tests;

// Ported from launch-mcp.test.mjs. A fake process runner stands in for `dotnet build`; no real build
// or process ever runs.
public class LaunchMcpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aitm-launch-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeRunner((string, string, int) result) : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Args)> Calls { get; } = [];

        public (string Stdout, string Stderr, int ExitCode) Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null)
        {
            Calls.Add((fileName, args));
            return result;
        }
    }

    [Fact]
    public void BuildsMcpDllIntoBinNextToTheScriptWhenMissingAndNoPluginData()
    {
        string source = Path.Combine(_dir, "mcp.cs");
        File.WriteAllText(source, "class A {}");
        FakeRunner runner = new(("", "", 0));

        bool ok = LaunchMcp.EnsureBuilt(_dir, null, source, runner);

        Assert.True(ok);
        Assert.Single(runner.Calls);
        (string fileName, IReadOnlyList<string> args) = runner.Calls[0];
        Assert.Equal("dotnet", fileName);
        Assert.Equal(["build", source, "-c", "Release", "-o", Path.Combine(_dir, "bin")], args);
    }

    [Fact]
    public void BuildsIntoPluginDataBinWhenSetNeverTheCheckout()
    {
        string source = Path.Combine(_dir, "mcp.cs");
        File.WriteAllText(source, "class A {}");
        string dataDir = Path.Combine(_dir, "plugin-data");
        FakeRunner runner = new(("", "", 0));

        LaunchMcp.EnsureBuilt(_dir, dataDir, source, runner);

        (_, IReadOnlyList<string> args) = runner.Calls[0];
        Assert.Equal(Path.Combine(dataDir, "bin"), args[^1]);
    }

    [Fact]
    public void SkipsTheBuildEntirelyWhenMcpDllAlreadyExists()
    {
        string source = Path.Combine(_dir, "mcp.cs");
        File.WriteAllText(source, "class A {}");
        string bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "mcp.dll"), "old");
        BuildStamp.WriteStamp(bin, source);
        FakeRunner runner = new(("", "", 0));

        bool ok = LaunchMcp.EnsureBuilt(_dir, null, source, runner);

        Assert.True(ok);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void ExitsNonZeroAndNeverLaunchesWhenTheBuildFails()
    {
        string source = Path.Combine(_dir, "mcp.cs");
        File.WriteAllText(source, "class A {}");
        FakeRunner runner = new(("", "error", 1));

        bool ok = LaunchMcp.EnsureBuilt(_dir, null, source, runner);

        Assert.False(ok);
        Assert.False(File.Exists(Path.Combine(_dir, "bin", "build-stamp.txt")));
    }
}
