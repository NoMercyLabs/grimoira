using Aitm.Store.Data;
using System.Text.RegularExpressions;

namespace Aitm.Brain.Data;

/// <summary>
/// The write-side brake on a node write (design checklist: "staged learnings are validated (NodeGuard)
/// before they are written"). The CLI and MCP callers ship two different guards today, each with its own
/// wording tuned for its own caller (a human typing a CLI verb versus an agent calling a tool with
/// positional key/a/b/c parameters) — kept as separate methods rather than unified, the same way
/// <c>QueryTool</c> keeps its CLI and MCP shapes apart (RESTRUCTURE.md section 2.2).
/// </summary>
public static partial class NodeGuard
{
    private static readonly string[] NodeKinds =
    {
        "rule", "fact", "concept", "symbol", "finding", "contract",
        "seam", "codekind", "project", "reference", "platform", "layer",
    };

    /// <summary>Copied verbatim from aitm.cs's <c>NodeGuard</c> (aitm.cs:418).</summary>
    public static string ValidateCli(string kind, string label)
    {
        if (!ValidNodeKind().IsMatch(kind))
            return "rejected: node kind must be a short lowercase vocab token (rule/fact/concept/symbol/finding/contract/seam/codekind/project/reference/platform/layer). Yours looks like prose — order is <k> <kind> <label>, the statement goes in --gloss.";
        if (label.Length > 120)
            return "rejected: label must be a short noun phrase (max 120 chars) — the full statement belongs in --gloss.";
        return "";
    }

    /// <summary>Copied verbatim from mcp.cs's <c>NodeGuard</c> (mcp.cs:62), including its longer,
    /// example-carrying rejection text (mcp.cs's <c>Usage</c>, mcp.cs:49) — the richer of the two because
    /// an agent, unlike a human re-reading a CLI's own help text, has to get the shape right without a
    /// second look.</summary>
    public static string ValidateMcp(string kind, string label)
    {
        if (!ValidNodeKind().IsMatch(kind))
            return Usage($"\"{Trim(kind)}\" is not a node kind — it looks like prose, and a=<node kind> is a single short token.");
        if (label.Length > 120)
            return Usage($"label is {label.Length} chars; it must be a short noun phrase (max 120). The full statement goes in c.");
        return "";
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..60] + "…";

    private static string Usage(string offending) =>
        $"rejected: {offending}\n" +
        "kind is the ROW TYPE, one of node | triple | slot. It is NOT the node's own kind.\n" +
        "  node:   kind=\"node\"   key=<stable-id>  a=<node kind>  b=<short label>  c=<full statement>\n" +
        "  triple: kind=\"triple\" key=<subject>    a=<predicate>  b=<object>      because=<why>\n" +
        "  slot:   kind=\"slot\"   key=<frame>      a=<name>       b=<value>\n" +
        $"node kinds: {string.Join(" / ", NodeKinds)}\n" +
        "example: kind=\"node\" key=\"nvenc-windows-native\" a=\"fact\" " +
        "b=\"NVENC encodes natively on Windows only\" c=\"Confirmed on real hardware: 3.03x, exit 0, 345 KB output. Never through WSL.\"";

    [GeneratedRegex("^[a-z0-9_-]{2,30}$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ValidNodeKind();
}
