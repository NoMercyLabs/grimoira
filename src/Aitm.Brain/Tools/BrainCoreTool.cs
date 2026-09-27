using Aitm.Brain.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// Always-on slice — the hard=1 nodes that replace the 24KB always-loaded MEMORY.md. Pulled at turn
/// start. CLI sub-verb <c>brain core</c> and MCP tool <c>brain_core</c> are one job with two shapes today
/// (RESTRUCTURE.md section 2.2), so this tool carries both, each copied verbatim from its own oracle:
/// <c>ExecuteCli</c> from aitm.cs's <c>BrainCore</c> (aitm.cs:1423), <c>ExecuteMcp</c> from mcp.cs's
/// <c>brain_core</c> (mcp.cs:656). Neither side reinforces — the hard nodes are always shown regardless
/// of use.
/// </summary>
public sealed class BrainCoreTool : ITool
{
    private const int CoreCap = 6000;

    public string Name => "brain core";
    public string CliVerb => "brain core";
    public string McpName => "brain_core";
    public string Help =>
        "brain core                            the hard=1 nodes that apply every turn. No args. " +
        "Pull at the START of a session/turn before doing project work.";

    public string ExecuteCli(SqliteConnection connection)
    {
        using SqliteCommand c = connection.CreateCommand();
        c.CommandText = "SELECT k, kind, label, gloss FROM node_now WHERE hard = 1 ORDER BY scheme, kind, k";
        using SqliteDataReader r = c.ExecuteReader();
        // PrintReader builds its output with AppendLine, so the returned string already ends with one
        // line terminator (aitm.cs's old BrainCore printed each row with its own Console.WriteLine
        // instead, ending with exactly one terminator too) — the CLI dispatch wraps this return value in
        // one more Console.WriteLine, so without stripping, the terminator would double into an extra
        // blank line the old CLI never had (same fix as part 2's MemTool/DocTool/RecallTool).
        return StripOneTrailingNewLine(BrainCliRows.PrintReader(r));
    }

    private static string StripOneTrailingNewLine(string s) =>
        s.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? s[..^Environment.NewLine.Length] : s;

    public string ExecuteMcp(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT k, label, gloss FROM node_now WHERE hard = 1 ORDER BY scheme, kind, k";
        return BrainMcpRows.Rows(cmd, CoreCap);
    }
}
