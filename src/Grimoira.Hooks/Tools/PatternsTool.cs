using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Hooks.Tools;

/// <summary>
/// Lists what <see cref="PatternWatchTool"/> has recorded: the read side the card asks for now that
/// pattern-watch itself only records and no longer prints a nudge (RESTRUCTURE.md slice 22, "Hooks,
/// part 3"; docs/RESTRUCTURE.md:268). Grimoira.Hooks owns the <c>patterns</c> table (RESTRUCTURE.md:335),
/// so the read tool sits here too, beside the handler that writes it. The query and the formatting
/// (grouped by "sequence" then "command", the "worth codifying"/"[settled]" flags) match
/// <c>brain-path --patterns</c> (a separate, already-ported reader over the same table from slice 19)
/// so the two never disagree about what counts as worth codifying.
/// </summary>
public sealed class PatternsTool : ITool
{
    public string Name => "patterns";
    public string CliVerb => "patterns";
    public string McpName => "patterns";
    public bool IsReadOnly => true;
    public string Help => "patterns                              commands/procedures recorded often enough to codify";

    /// <summary>The MCP side is the same read: the registry binds the connection, there are no arguments.</summary>
    public string ExecuteMcp(SqliteConnection connection) => ExecuteCli(connection);

    /// <summary>Group header per kind, in the order shown: procedures first, then commands, then the
    /// read side (files, greps, globs) the counter learned from the PostToolUse search tools.</summary>
    private static readonly (string Kind, string Header)[] Groups =
    [
        ("sequence", "procedures:"),
        ("command", "single commands:"),
        ("read", "files read:"),
        ("grep", "greps:"),
        ("glob", "globs:"),
    ];

    public string ExecuteCli(SqliteConnection connection)
    {
        List<(string Sig, long Count, bool Promoted, string Kind)> rows = [];
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sig, count, promoted, COALESCE(kind,'command') AS kind " +
                "FROM patterns ORDER BY (kind='sequence') DESC, count DESC LIMIT 60";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.GetInt64(1), !r.IsDBNull(2) && r.GetInt64(2) != 0, r.GetString(3)));
        }
        catch (SqliteException)
        {
            rows = [];
        }
        if (rows.Count == 0) return "nothing recorded yet.";

        List<string> lines = [];
        foreach ((string group, string header) in Groups)
        {
            List<(string Sig, long Count, bool Promoted, string Kind)> of = [.. rows.Where(r => r.Kind == group)];
            if (of.Count == 0) continue;
            lines.Add("");
            lines.Add(header);
            foreach ((string sig, long count, bool promoted, string kind) in of)
            {
                string flag = promoted ? " [settled]" : (count >= (group == "sequence" ? 3 : 5) ? " <- worth codifying" : "");
                lines.Add($"  {count,3}x  [{kind}] {sig}{flag}");
            }
        }
        return string.Join("\n", lines).TrimStart('\n');
    }
}
