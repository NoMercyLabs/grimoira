using Grimoira.Store.Data;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimoira.Layout.Tests;

// RESTRUCTURE.md slice 24 bullet 1: "grimoira.cs and mcp.cs now hold only dispatch to the registry."
// Slice 24's own passes moved every verb's logic into a tool class; this guard is what keeps a later
// change from quietly growing a new piece of inline logic back into either host file instead of adding
// it to the tool it belongs to.
//
// The rule: every top-level local function declared in grimoira.cs, and every `private static` helper
// declared on mcp.cs's GrimoiraTools, must be named on the allow-list below with a one-line reason. A name
// found in the file but missing from the allow-list fails the test (a new function crept in); a name on
// the allow-list no longer found in the file also fails (the list rotted and should shrink).
public partial class LocalHelperAllowListTests
{
    // Matches a top-level local-function declaration in grimoira.cs's global-statements body: one of the
    // return types every local function here actually uses, immediately followed by a name and "(".
    // Anchored to column 0 (RegexOptions.Multiline "^") because every local function in grimoira.cs is
    // declared unindented; anything indented is a statement inside a case/try block, not a declaration.

    // Matches a `private static` helper on mcp.cs's GrimoiraTools class (4-space indent, one level inside
    // the class). The [McpServerTool]-attributed methods are all `public static` and so never match.

    // grimoira.cs: empty since slice 29b. grimoira.cs is a shim that calls Grimoira.Server's CliDispatch.Run; all 21
    // local functions it had (argument parsing, instance resolution, DB/schema plumbing, BrainCmd and the
    // BRAIN recall/impact/stats engine) moved with the dispatch body into CliDispatch as private methods,
    // unchanged. The list stays so a local function that creeps back into the shim fails this test.
    private static readonly Dictionary<string, string> GrimoiraAllowList = new(StringComparer.Ordinal);

    // mcp.cs: every [McpServerTool] method opens its own connection then delegates to a tool class: Open
    // (+ its MaybeMaintain piggyback) and instance resolution are the plumbing that has to run first.
    private static readonly Dictionary<string, string> McpAllowList = new(StringComparer.Ordinal)
    {
        ["Open"] = "dispatch plumbing: opens the per-instance connection every tool method delegates through",
        ["MaybeMaintain"] = "dispatch plumbing: time-gated maintenance piggybacked on every Open() call",
        ["ResolveInstance"] = "instance resolution: same role as grimoira.cs's copy, scoped to the MCP host",
        ["Slug"] = "instance resolution: normalizes ResolveInstance's raw text, same role as grimoira.cs's copy",
    };

    [Fact]
    public void EveryLocalFunctionInGrimoiraCsIsOnTheAllowList()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "grimoira.cs"));
        List<string> found = [.. GrimoiraLocalFunctionDeclaration().Matches(source).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

        List<string> unknown = [.. found.Where(n => !GrimoiraAllowList.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal)];
        Assert.True(unknown.Count == 0,
            "grimoira.cs has local function(s) not on the allow-list (dispatch glue only — add the tool-class "
            + "call instead, or add the name to LocalHelperAllowListTests.GrimoiraAllowList with a reason):\n"
            + string.Join("\n", unknown));

        List<string> stale = [.. GrimoiraAllowList.Keys.Where(n => !found.Contains(n)).OrderBy(n => n, StringComparer.Ordinal)];
        Assert.True(stale.Count == 0,
            "GrimoiraAllowList names a local function no longer in grimoira.cs — shrink the list:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void EveryPrivateHelperInMcpCsIsOnTheAllowList()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "mcp.cs"));
        List<string> found = [.. McpPrivateHelperDeclaration().Matches(source).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

        List<string> unknown = [.. found.Where(n => !McpAllowList.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal)];
        Assert.True(unknown.Count == 0,
            "mcp.cs has a private helper not on the allow-list (dispatch glue only — add the tool-class "
            + "call instead, or add the name to LocalHelperAllowListTests.McpAllowList with a reason):\n"
            + string.Join("\n", unknown));

        List<string> stale = [.. McpAllowList.Keys.Where(n => !found.Contains(n)).OrderBy(n => n, StringComparer.Ordinal)];
        Assert.True(stale.Count == 0,
            "McpAllowList names a private helper no longer in mcp.cs — shrink the list:\n" + string.Join("\n", stale));
    }

    [GeneratedRegex(@"(?m)^(?:void|string\??|long|int|bool|double|List<[\w<>,\?\s]+>|IEnumerable<[\w<>,\?\s]+>)\s+([A-Za-z_]\w*)\s*\(", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex GrimoiraLocalFunctionDeclaration();
    [GeneratedRegex(@"(?m)^ {4}private static (?:async\s+)?[\w<>\?\[\],\s]+?\s+([A-Za-z_]\w*)\s*\(", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex McpPrivateHelperDeclaration();
}
