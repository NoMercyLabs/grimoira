using Grimora.Store.Data;
using System.Text.RegularExpressions;
using Grimora.Cli.Tools;
using Xunit;

namespace Grimora.Layout.Tests;

// RESTRUCTURE.md sub-card 29e: the thin client (Grimora.Cli.Tools.ThinClient) and the scripts that spawn it
// (init.mjs) each wait for POST /cli with their own client-side timeout, but the answer they wait for is
// bounded server-side by CliEndpoint.LongTimeout. Before the thin client existed, a long verb ran in the
// caller's own process, so only the caller's own timeout mattered; index-chat's chat import could run for
// the full 900 s init.mjs gives it. Once the verb moved behind POST /cli, the server's own limit governs
// too, and a server limit shorter than the caller's own wait would time out an import the old CLI would
// have finished — a regression a code review reading either file alone would miss. CliEndpoint is
// internal and init.mjs is JavaScript, so those two are read as text; ThinClient.RequestTimeout is read
// directly.
public partial class CliClientTimeoutsCoverTheServerLimitTests
{
    private static int LongTimeoutSeconds()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Grimora.Server", "Data", "CliEndpoint.cs"));
        Match match = LongTimeoutDeclaration().Match(source);
        Assert.True(match.Success, "CliEndpoint.LongTimeout was not found; the regex needs updating to match its new shape.");
        return int.Parse(match.Groups[1].Value);
    }

    private static int IndexChatTimeoutMs()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "init.mjs"));
        Match match = IndexChatRunCliTimeout().Match(source);
        Assert.True(match.Success, "init.mjs's index-chat runCli call was not found; the regex needs updating to match its new shape.");
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
        int thinClientSeconds = (int)ThinClient.RequestTimeout.TotalSeconds;
        int serverSeconds = LongTimeoutSeconds();

        Assert.True(thinClientSeconds >= serverSeconds,
            $"ThinClient.RequestTimeout is {thinClientSeconds} s but CliEndpoint.LongTimeout is {serverSeconds} s; " +
            "the thin client would give up on a long verb before the server's own 124 answer could arrive.");
    }

    [GeneratedRegex(@"LongTimeout = TimeSpan\.FromSeconds\((\d+)\)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex LongTimeoutDeclaration();
    [GeneratedRegex(@"runCli\(\['index-chat',[^\]]*\],\s*(\d+)\)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex IndexChatRunCliTimeout();
}
