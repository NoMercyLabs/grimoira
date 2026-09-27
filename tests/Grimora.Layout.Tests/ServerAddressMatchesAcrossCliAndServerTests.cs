using Xunit;

namespace Grimora.Layout.Tests;

// RESTRUCTURE.md Slice P1: the service listens on a per-user, per-realm local pipe / socket instead of
// 127.0.0.1:7635. The pipe name (Windows) and socket path (macOS/Linux) are derived from the data
// directory ("realm") the same way on both sides of the pipe: Grimora.Cli.Tools.ServerAddress (the client)
// and Grimora.Server.Data.ServerAddress (the listener) cannot share a type (Grimora.Cli and Grimora.Server sit at
// the same reference level; ReferenceDirectionTests.EveryReferencePointsToALowerLevel forbids either
// referencing the other, and CliReferencesNothingInGrimora forbids Cli referencing anything in Grimora at all).
// This test is the only thing that would notice the two copies drifting apart.
public sealed class ServerAddressMatchesAcrossCliAndServerTests
{
    [Theory]
    [InlineData("C:/Users/test/.grimora")]
    [InlineData("/home/test/.grimora")]
    [InlineData("C:/Users/test/.grimora-other-realm")]
    public void PipeNameIsIdenticalOnBothSides(string dataDir)
    {
        Assert.Equal(Grimora.Cli.Tools.ServerAddress.PipeName(dataDir), Grimora.Server.Data.ServerAddress.PipeName(dataDir));
    }

    [Theory]
    [InlineData("C:/Users/test/.grimora")]
    [InlineData("/home/test/.grimora")]
    public void SocketPathIsIdenticalOnBothSides(string dataDir)
    {
        Assert.Equal(Grimora.Cli.Tools.ServerAddress.SocketPath(dataDir), Grimora.Server.Data.ServerAddress.SocketPath(dataDir));
    }

    [Fact]
    public void DifferentDataDirsGetDifferentPipeNames()
    {
        string a = Grimora.Cli.Tools.ServerAddress.PipeName("C:/Users/test/.grimora");
        string b = Grimora.Cli.Tools.ServerAddress.PipeName("C:/Users/test/.grimora-other-realm");

        Assert.NotEqual(a, b);
    }

    // The same folder spelled differently must be the same realm, or a client and a server that resolved the
    // path two ways would never find each other.
    [Fact]
    public void ATrailingSeparatorDoesNotChangeThePipeName()
    {
        string a = Grimora.Cli.Tools.ServerAddress.PipeName("C:/Users/test/.grimora");
        string b = Grimora.Cli.Tools.ServerAddress.PipeName("C:/Users/test/.grimora/");
        Assert.Equal(a, b);
        Assert.Equal(a, Grimora.Server.Data.ServerAddress.PipeName("C:/Users/test/.grimora/"));

        // A backslash is a separator only on Windows; on Linux it is an ordinary file-name character.
        if (OperatingSystem.IsWindows())
            Assert.Equal(a, Grimora.Server.Data.ServerAddress.PipeName("C:/Users/test/.grimora\\"));
    }

    [Fact]
    public void OnWindowsTheCaseOfThePathDoesNotChangeThePipeName()
    {
        if (!OperatingSystem.IsWindows()) return; // other file systems are case-sensitive: two realms

        Assert.Equal(
            Grimora.Cli.Tools.ServerAddress.PipeName("C:/Users/Test/.Grimora"),
            Grimora.Server.Data.ServerAddress.PipeName("c:/users/test/.grimora"));
    }

    [Theory]
    [InlineData(null, "/home/u", "/home/u/.grimora")]
    [InlineData("", "/home/u", "/home/u/.grimora")]
    [InlineData("/data/x", "/home/u", "/data/x")]
    public void TheDataDirRuleTreatsAnEmptyValueAsUnsetOnBothSides(string? configured, string home, string expected)
    {
        string want = Path.Combine(home, ".grimora");
        if (expected != "/data/x") expected = want;
        Assert.Equal(expected, Grimora.Cli.Tools.ServerAddress.ResolveDataDir(configured, home));
        Assert.Equal(expected, Grimora.Server.Data.ServerAddress.ResolveDataDir(configured, home));
    }
}
