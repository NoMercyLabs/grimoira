using Grimora.TestSupport;
using Xunit;

namespace Grimora.Server.Tests;

/// <summary>
/// The parity oracles (HttpSnapshotParityTests, GrimoraMcpParityTests, McpSnapshotParityTests) drive the
/// pinned pre-slice-24 mcp.dll through <see cref="McpProcess.Run"/> and compare its text with today's
/// server, which the tests call with real, sequentially awaited McpClient calls. That comparison only
/// holds when the oracle is driven the same way. The old snapshot's <c>brain_flush</c> is a bare
/// File.Exists / ReadAllLines / File.Delete on <c>pending-learn.jsonl</c> with no lock, and the MCP host
/// runs pipelined <c>tools/call</c>s concurrently, so two flushes sent back to back could throw inside the
/// snapshot ("An error occurred invoking 'brain_flush'.") — seen once in 20 full-suite runs. The fake
/// server's reply for a call names every call id that had arrived before it answered; a driver that waits
/// for each reply never lets the next id show up there.
/// </summary>
public sealed class McpProcessDrivesOneCallAtATimeTests
{
    [Fact]
    public void TheNextCallIsSentOnlyAfterThePreviousReply()
    {
        string fake = FakeMcpServer.EnsureBuilt();

        (_, IReadOnlyList<string> results) = McpProcess.Run(fake, "unused",
            [("first", new { }), ("second", new { }), ("third", new { })]);

        Assert.Equal("arrived-before-reply:100", results[0]);
        Assert.Equal("arrived-before-reply:100,101", results[1]);
        Assert.Equal("arrived-before-reply:100,101,102", results[2]);
    }

    [Fact]
    public void PipelinedModeStillSendsEveryCallUpFront()
    {
        string fake = FakeMcpServer.EnsureBuilt();

        (_, IReadOnlyList<string> results) = McpProcess.Run(fake, "unused",
            [("first", new { }), ("second", new { })], pipelined: true);

        Assert.Equal("arrived-before-reply:100,101", results[0]);
    }
}
