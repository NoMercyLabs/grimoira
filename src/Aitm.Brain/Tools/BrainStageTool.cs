using System.Collections.Concurrent;
using Aitm.Brain.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// Per-ledger-path async gate. Over MCP stdio, a client is free to pipeline requests — send
/// <c>brain_stage</c> then <c>brain_flush</c> (then a second <c>brain_flush</c>) without waiting for
/// each reply — and the MCP host dispatches each <c>tools/call</c> concurrently rather than one at a
/// time. Two concurrent calls hitting the SAME <c>pending-learn.jsonl</c> would otherwise race its
/// read-modify-write (<see cref="BrainStageTool.ExecuteMcp"/>'s append, <see cref="BrainFlushTool.ExecuteMcp"/>'s
/// read-then-delete-or-rewrite): observed as a flush reading the file mid-write ("nothing staged." when a
/// learning was in fact staged), two flushes applying the same staged line twice (a
/// <c>UNIQUE constraint failed</c> reject), or a flush's read racing another flush's delete (an unhandled
/// <see cref="IOException"/>, surfaced to the MCP client as "An error occurred invoking 'brain_flush'").
/// Keyed by the ledger's full path so unrelated instances never block each other; the HTTP path needs no
/// equivalent because <see cref="Aitm.Server.Data.LockingAIFunction"/> already serializes every tool call
/// per project instance before it reaches here.
/// </summary>
internal static class LedgerGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim For(string ledgerPath) =>
        Gates.GetOrAdd(Path.GetFullPath(ledgerPath), static _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// BRAIN/write-side brake, "stage" half: append a durable learning to the per-instance
/// <c>pending-learn.jsonl</c> ledger without writing it (a cheap append, no DB write). CLI verb
/// <c>stage</c> (with sub-verbs <c>node</c>/<c>triple</c>/<c>slot</c>/<c>list</c>/<c>clear</c>/
/// <c>dismiss</c>) and MCP tool <c>brain_stage</c> are one job with two shapes today (RESTRUCTURE.md
/// section 2.2). <c>ExecuteCli</c> is copied verbatim from aitm.cs's <c>StageCmd</c> (aitm.cs:1913).
/// <c>ExecuteMcp</c> is copied verbatim from mcp.cs's <c>brain_stage</c> (mcp.cs:924), including the
/// row-kind/node-kind coercion (mcp.cs:936-938) that fires whenever a caller passes the node's own kind
/// (e.g. "rule") as the row type. <see cref="BrainFlushTool"/> commits the ledger this tool appends to.
/// </summary>
public sealed class BrainStageTool : ITool
{
    // Disjoint by construction (RESTRUCTURE.md/mcp.cs comment), which is what makes the MCP-side
    // coercion below safe: a value can only ever match one of the two lists.
    private static readonly string[] NodeKinds =
    {
        "rule", "fact", "concept", "symbol", "finding", "contract",
        "seam", "codekind", "project", "reference", "platform", "layer",
    };
    private static readonly string[] RowKinds = { "node", "triple", "slot" };

    public string Name => "stage";
    public string CliVerb => "stage";
    public string? McpName => "brain_stage";
    public string Help =>
        "stage <node|triple|slot|list|clear|dismiss> … : append a durable learning to pending-learn.jsonl " +
        "without writing it (`aitm flush` / brain_flush commits the batch). " +
        "MCP brain_stage(kind,key,a,b,c,because,hard): same args as brain_learn.";

    /// <summary>The ledger sits next to the store, exactly where aitm.cs's <c>root</c> and mcp.cs's
    /// <c>LedgerPath()</c> both put it (<c>~/.aitm/&lt;instance&gt;/pending-learn.jsonl</c>) — derived
    /// here from the open connection's own file, the same way <c>ShedDocTool.ForgetSynthesisIndex</c>
    /// finds its sibling file.</summary>
    public static string LedgerPath(SqliteConnection connection)
    {
        string? dataSource = connection.DataSource;
        string dir = string.IsNullOrEmpty(dataSource) ? "." : Path.GetDirectoryName(Path.GetFullPath(dataSource)) ?? ".";
        return Path.Combine(dir, "pending-learn.jsonl");
    }

    public string ExecuteCli(SqliteConnection connection, IReadOnlyList<string> pos, string gloss = "", string scheme = "", string facet = "text", string because = "", bool hard = false, bool multi = false)
    {
        string J(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ") + "\"";
        string sub = pos.Count > 0 ? pos[0] : "list";
        string ledger = LedgerPath(connection);
        switch (sub)
        {
            case "node":
                if (pos.Count < 4) return "usage: stage node <k> <kind> <label> [--gloss ..] [--scheme ..] [--hard]";
                string stageGuard = NodeGuard.ValidateCli(pos[2], string.Join(' ', pos.Skip(3)));
                if (stageGuard.Length > 0) return stageGuard;
                File.AppendAllText(ledger, "{" + $"\"k\":\"node\",\"key\":{J(pos[1])},\"kind\":{J(pos[2])},\"label\":{J(string.Join(' ', pos.Skip(3)))},\"gloss\":{J(gloss)},\"scheme\":{J(scheme)},\"hard\":{(hard ? "true" : "false")}" + "}\n");
                return $"staged node {pos[1]} (owe flush).";
            case "triple":
                if (pos.Count < 4) return "usage: stage triple <s> <predicate> <o> [--because ..] [--hard]";
                File.AppendAllText(ledger, "{" + $"\"k\":\"triple\",\"s\":{J(pos[1])},\"p\":{J(pos[2])},\"o\":{J(pos[3])},\"because\":{J(because)},\"hard\":{(hard ? "true" : "false")}" + "}\n");
                return $"staged triple {pos[1]} {pos[2]} {pos[3]} (owe flush).";
            case "slot":
                if (pos.Count < 4) return "usage: stage slot <frame> <name> <value> [--facet ..] [--multi] [--because ..]";
                File.AppendAllText(ledger, "{" + $"\"k\":\"slot\",\"frame\":{J(pos[1])},\"name\":{J(pos[2])},\"value\":{J(string.Join(' ', pos.Skip(3)))},\"facet\":{J(facet)},\"multi\":{(multi ? "true" : "false")},\"because\":{J(because)}" + "}\n");
                return $"staged slot {pos[1]}.{pos[2]} (owe flush).";
            case "list":
                return File.Exists(ledger) && File.ReadAllText(ledger).Trim().Length > 0 ? File.ReadAllText(ledger).TrimEnd() : "nothing staged.";
            case "clear":
            case "dismiss":
                if (File.Exists(ledger)) File.Delete(ledger);
                return "staged learnings cleared (nothing persisted).";
            default:
                return "stage <node|triple|slot|list|clear> …";
        }
    }

    public string ExecuteMcp(SqliteConnection connection, string kind, string key, string a = "", string b = "", string c = "", string because = "", bool hard = false)
    {
        string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");

        // See the class comment: the commonest miss is passing the node's own kind ("rule") as the row
        // type. Row types and node kinds are disjoint, so "rule" could only ever have meant a node of
        // kind rule — accept it, shifting the remaining arguments left by the slot the caller left empty.
        bool coerced = !RowKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)
            && NodeKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);
        if (coerced) (kind, a, b, c) = ("node", kind, a, b.Length > 0 ? b : c);

        if (kind == "node")
        {
            string guardErr = NodeGuard.ValidateMcp(a, b.Length > 0 ? b : key);
            if (guardErr.Length > 0) return guardErr;
        }
        string h = hard ? "true" : "false";
        string line = kind switch
        {
            "node" => "{" + $"\"k\":\"node\",\"key\":\"{Esc(key)}\",\"kind\":\"{Esc(a)}\",\"label\":\"{Esc(b)}\",\"gloss\":\"{Esc(c)}\",\"scheme\":\"\",\"hard\":{h}" + "}",
            "triple" => "{" + $"\"k\":\"triple\",\"s\":\"{Esc(key)}\",\"p\":\"{Esc(a)}\",\"o\":\"{Esc(b)}\",\"because\":\"{Esc(because)}\",\"hard\":{h}" + "}",
            "slot" => "{" + $"\"k\":\"slot\",\"frame\":\"{Esc(key)}\",\"name\":\"{Esc(a)}\",\"value\":\"{Esc(b)}\",\"facet\":\"text\",\"multi\":false,\"because\":\"{Esc(because)}\"" + "}",
            _ => "",
        };
        if (line.Length == 0) return WrongRowKind(kind);
        // A brand-new instance has no directory yet, and AppendAllText does not make one.
        string ledger = LedgerPath(connection);
        SemaphoreSlim gate = LedgerGate.For(ledger);
        gate.Wait();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
            File.AppendAllText(ledger, line + "\n");
        }
        finally
        {
            gate.Release();
        }
        return coerced
            ? $"staged node {key} as kind \"{a}\" (owe brain_flush). Note: the first argument is the ROW type — node / triple / slot — and \"{a}\" is the node's own kind, so it was moved for you."
            : $"staged {kind} {key} (owe brain_flush).";
    }

    private static string WrongRowKind(string kind) =>
        NodeKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)
            ? Usage($"you passed \"{kind}\" as kind, but that is a NODE kind. Use kind=\"node\" and move \"{kind}\" into a.")
            : Usage($"\"{Trim(kind)}\" is not a row type.");

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
}
