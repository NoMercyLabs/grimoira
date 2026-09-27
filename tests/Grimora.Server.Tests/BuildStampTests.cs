using Grimora.Server.Data;
using Xunit;

namespace Grimora.Server.Tests;

// Ported from build-stamp.test.mjs: a plugin update must rebuild the tool server when its source
// changed, and must not rebuild when it did not.
public class BuildStampTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("grimora-stamp-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void NoBuildYetIsStale()
    {
        string source = Path.Combine(_dir, "mcp.cs");
        string bin = Path.Combine(_dir, "bin");
        File.WriteAllText(source, "class A {}");

        Assert.True(BuildStamp.NeedsBuild(bin, source));
    }

    [Fact]
    public void BuildWithoutAStampIsStale()
    {
        string source = Path.Combine(_dir, "mcp.cs");
        string bin = Path.Combine(_dir, "bin");
        File.WriteAllText(source, "class A {}");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "mcp.dll"), "old");

        Assert.True(BuildStamp.NeedsBuild(bin, source));
    }

    [Fact]
    public void StampedBuildOfTheSameSourceIsFresh()
    {
        string source = Path.Combine(_dir, "mcp.cs");
        string bin = Path.Combine(_dir, "bin");
        File.WriteAllText(source, "class A {}");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "mcp.dll"), "old");

        BuildStamp.WriteStamp(bin, source);

        Assert.False(BuildStamp.NeedsBuild(bin, source));
    }

    [Fact]
    public void SourceChangedByAnUpdateIsStale()
    {
        string source = Path.Combine(_dir, "mcp.cs");
        string bin = Path.Combine(_dir, "bin");
        File.WriteAllText(source, "class A {}");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "mcp.dll"), "old");
        BuildStamp.WriteStamp(bin, source);

        File.WriteAllText(source, "class A { void GraphQuery() {} }");

        Assert.True(BuildStamp.NeedsBuild(bin, source));
    }

    [Fact]
    public void NoSourceToBuildFromKeepsTheExistingBuild()
    {
        string source = Path.Combine(_dir, "mcp.cs");
        string bin = Path.Combine(_dir, "bin");
        File.WriteAllText(source, "class A {}");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "mcp.dll"), "old");
        BuildStamp.WriteStamp(bin, source);

        File.Delete(source);

        Assert.False(BuildStamp.NeedsBuild(bin, source));
    }
}
