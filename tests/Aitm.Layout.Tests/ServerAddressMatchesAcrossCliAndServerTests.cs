using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md Slice P1: the service listens on a per-user, per-realm local pipe / socket instead of
// 127.0.0.1:7635. The pipe name (Windows) and socket path (macOS/Linux) are derived from the data
// directory ("realm") the same way on both sides of the pipe: Aitm.Cli.Tools.ServerAddress (the client)
// and Aitm.Server.Data.ServerAddress (the listener) cannot share a type (Aitm.Cli and Aitm.Server sit at
// the same reference level; ReferenceDirectionTests.EveryReferencePointsToALowerLevel forbids either
// referencing the other, and CliReferencesNothingInAitm forbids Cli referencing anything in Aitm at all).
// This test is the only thing that would notice the two copies drifting apart.
public sealed class ServerAddressMatchesAcrossCliAndServerTests
{
    [Theory]
    [InlineData("C:/Users/test/.aitm")]
    [InlineData("/home/test/.aitm")]
    [InlineData("C:/Users/test/.aitm-other-realm")]
    public void PipeNameIsIdenticalOnBothSides(string dataDir)
    {
        Assert.Equal(Aitm.Cli.Tools.ServerAddress.PipeName(dataDir), Aitm.Server.Data.ServerAddress.PipeName(dataDir));
    }

    [Theory]
    [InlineData("C:/Users/test/.aitm")]
    [InlineData("/home/test/.aitm")]
    public void SocketPathIsIdenticalOnBothSides(string dataDir)
    {
        Assert.Equal(Aitm.Cli.Tools.ServerAddress.SocketPath(dataDir), Aitm.Server.Data.ServerAddress.SocketPath(dataDir));
    }

    [Fact]
    public void DifferentDataDirsGetDifferentPipeNames()
    {
        string a = Aitm.Cli.Tools.ServerAddress.PipeName("C:/Users/test/.aitm");
        string b = Aitm.Cli.Tools.ServerAddress.PipeName("C:/Users/test/.aitm-other-realm");

        Assert.NotEqual(a, b);
    }
}
