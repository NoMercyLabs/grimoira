using Xunit;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md "Phase 4, replaced (the owner, 2026-09-26)": "The service owns the store and is reached
// only through a local pipe: a named pipe on Windows, a Unix domain socket on macOS/Linux, created so
// only the current user can open it. No TCP port, no HTTP listener on the network, no token, no
// Host/Origin guard." Slice P1 card: "no TCP listener remains (a test asserts it)."
//
// These are source-text assertions, the same style as the old TheServerListensOnLoopbackOnly test they
// replace: nothing here can simulate another OS user opening the pipe, so the ACL/mode a real run would
// enforce is asserted as configuration, not as a live denial (the Slice P1 card allows this explicitly).
public sealed class PipeOnlyTransportTests
{
    private static readonly string ProgramSource =
        File.ReadAllText(Path.Combine(Aitm.Layout.Tests.RepoPaths.Root, "src", "Aitm.Server", "Data", "Program.cs"));

    [Fact]
    public void TheServerNeverBindsATcpUrl()
    {
        Assert.DoesNotContain("UseUrls", ProgramSource);
        Assert.DoesNotContain("ListenAnyIP", ProgramSource);
        Assert.DoesNotContain("ListenLocalhost", ProgramSource);
        Assert.DoesNotContain("ListenTcp", ProgramSource);
        Assert.DoesNotContain("0.0.0.0", ProgramSource);
    }

    [Fact]
    public void TheServerListensOnANamedPipeOnWindows()
    {
        Assert.Contains("ListenNamedPipe", ProgramSource);
    }

    [Fact]
    public void TheServerListensOnAUnixSocketElsewhere()
    {
        Assert.Contains("ListenUnixSocket", ProgramSource);
    }

    // "OS permissions limited to the current user": Kestrel's named-pipe transport restricts a pipe to the
    // current user by default (NamedPipeTransportOptions.CurrentUserOnly), but a default is silent and
    // easy to lose in a refactor; this pins it as an explicit, visible setting instead of relying on it.
    [Fact]
    public void TheNamedPipeIsExplicitlyRestrictedToTheCurrentUser()
    {
        Assert.Contains("CurrentUserOnly = true", ProgramSource);
    }
}
