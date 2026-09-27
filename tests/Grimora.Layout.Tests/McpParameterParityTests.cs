using Grimora.Store.Data;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimora.Layout.Tests;

/// <summary>
/// Permanent guard for the MCP parameter-parity audit: the CLI side already found a bug class where a
/// flag the old code read (<c>add --why</c>, <c>learn --facet</c>) was silently ignored once grimora.cs was
/// wired to a tool class. This test is the same check on the MCP side, made permanent: each of the 25
/// golden MCP tools' parameter list (name, type, order, default) is pinned to the last pre-dispatch
/// oracle (<c>bbb9b4d2f4788d3f1960438799331d57198c9fbe:mcp.cs</c>, the commit before mcp.cs's own methods
/// started delegating to Grimora.* tool classes) and must never silently drift.
///
/// The rule this enforces: a parameter may only ever be added, renamed, retyped, or have its default
/// changed as a deliberate, reviewed edit to mcp.cs's own signature — never as a side effect of a
/// dispatch/wiring refactor that leaves the attribute and method name untouched but quietly drops or
/// reshapes an argument on the way to (or inside) the delegated <c>ExecuteMcp</c>/HTTP path. Comparing
/// mcp.cs's own live signature against the oracle catches that even when the tool class behind it still
/// runs without throwing.
/// </summary>
public partial class McpParameterParityTests
{
    public const string PreDispatchOracleCommit = "bbb9b4d2f4788d3f1960438799331d57198c9fbe";


    [Fact]
    public void Every25McpToolParameterListMatchesThePreDispatchOracle()
    {
        string oracleSource = GitShow(PreDispatchOracleCommit, "mcp.cs");
        string currentSource = File.ReadAllText(Path.Combine(RepoPaths.Root, "mcp.cs"));

        Dictionary<string, string> oracle = ExtractSignatures(oracleSource);
        Dictionary<string, string> current = ExtractSignatures(currentSource);

        Assert.Equal(25, oracle.Count);
        foreach ((string tool, string oracleParams) in oracle.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Assert.True(current.TryGetValue(tool, out string? currentParams),
                $"MCP tool '{tool}' had a [McpServerTool] method in the pre-dispatch oracle but mcp.cs no " +
                "longer has a matching method — a tool disappeared from the wire, not just from this list.");
            Assert.True(oracleParams == currentParams,
                $"MCP tool '{tool}' changed shape: oracle mcp.cs@{PreDispatchOracleCommit[..7]} took " +
                $"({oracleParams}), mcp.cs now takes ({currentParams}). A parameter was added, renamed, " +
                "retyped, reordered, or had its default changed — confirm the new/removed/renamed " +
                "parameter is deliberate, and that it is honoured wherever mcp.cs dispatches (including " +
                "any tool class ExecuteMcp/HTTP wiring behind it), before updating this pin.");
        }
    }

    private static Dictionary<string, string> ExtractSignatures(string source)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        int index = 0;
        while ((index = source.IndexOf("[McpServerTool]", index, StringComparison.Ordinal)) >= 0)
        {
            Match m = ToolMethodSignatureWithParameters().Match(source, index);
            Assert.True(m.Success, $"no method signature found after [McpServerTool] at offset {index}");
            found[m.Groups[1].Value] = Normalize(m.Groups[2].Value);
            index = m.Index + m.Length;
        }
        return found;
    }

    // Collapses internal whitespace/newlines in a wrapped parameter list into one comparable line, but
    // never reorders or drops a parameter — a reordered or missing default is exactly what this guards.
    private static string Normalize(string parameters) =>
        string.Join(", ", parameters
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => WhitespaceRun().Replace(p, " ")));

    private static string GitShow(string commit, string relativePath)
    {
        ProcessStartInfo psi = new("git", $"show {commit}:{relativePath}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoPaths.Root,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("could not start git");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git show {commit}:{relativePath} failed: {stderr}");
        return stdout;
    }

    [GeneratedRegex(@"public static (?:async )?(?:Task<string>|string) (\w+)\s*\(([^)]*)\)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ToolMethodSignatureWithParameters();
    [GeneratedRegex(@"\s+", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex WhitespaceRun();
}
