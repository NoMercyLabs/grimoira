using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md sub-card 29e: the thin client (Aitm.Cli.Tools.ThinClient) and the scripts that spawn it
// (init.mjs) each wait for POST /cli with their own client-side timeout, but the answer they wait for is
// bounded server-side by CliEndpoint.LongTimeout. Before the thin client existed, a long verb ran in the
// caller's own process, so only the caller's own timeout mattered; index-chat's chat import could run for
// the full 900 s init.mjs gives it. Once the verb moved behind POST /cli, the server's own limit governs
// too, and a server limit shorter than the caller's own wait would time out an import the old CLI would
// have finished — a regression a code review reading either file alone would miss. These tests read both
// sides as text (not by referencing CliEndpoint, which is internal, and not by running a 900 s import) and
// assert one number against the other.
public class CliClientTimeoutsCoverTheServerLimitTests
{
    private static int LongTimeoutSeconds()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "Data", "CliEndpoint.cs"));
        Match match = Regex.Match(source, @"LongTimeout = TimeSpan\.FromSeconds\((\d+)\)");
        Assert.True(match.Success, "CliEndpoint.LongTimeout was not found; the regex needs updating to match its new shape.");
        return int.Parse(match.Groups[1].Value);
    }

    private static int IndexChatTimeoutMs()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "init.mjs"));
        Match match = Regex.Match(source, @"runCli\(\['index-chat',[^\]]*\],\s*(\d+)\)");
        Assert.True(match.Success, "init.mjs's index-chat runCli call was not found; the regex needs updating to match its new shape.");
        return int.Parse(match.Groups[1].Value);
    }

    private static int ThinClientRequestTimeoutSeconds()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Aitm.Cli", "Tools", "ThinClient.cs"));
        Match match = Regex.Match(source, @"RequestTimeout = TimeSpan\.FromSeconds\((\d+)\)");
        Assert.True(match.Success, "ThinClient.RequestTimeout was not found; the regex needs updating to match its new shape.");
        return int.Parse(match.Groups[1].Value);
    }

    [Fact]
    public void ServersLongVerbTimeoutIsAtLeastInitMjsGivesIndexChat()
    {
        int serverMs = LongTimeoutSeconds() * 1000;
        int callerMs = IndexChatTimeoutMs();

        Assert.True(serverMs >= callerMs,
            $"CliEndpoint.LongTimeout is {serverMs} ms but init.mjs waits {callerMs} ms for index-chat; " +
            "a large chat import would now time out at the server before the caller gives up on it.");
    }

    [Fact]
    public void ThinClientsOwnHttpTimeoutIsAtLeastTheServersLongVerbTimeout()
    {
        int thinClientSeconds = ThinClientRequestTimeoutSeconds();
        int serverSeconds = LongTimeoutSeconds();

        Assert.True(thinClientSeconds >= serverSeconds,
            $"ThinClient.RequestTimeout is {thinClientSeconds} s but CliEndpoint.LongTimeout is {serverSeconds} s; " +
            "the thin client would give up on a long verb before the server's own 124 answer could arrive.");
    }
}
