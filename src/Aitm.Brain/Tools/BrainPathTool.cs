using Aitm.Brain.Data;
using Aitm.Store.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Aitm.Brain.Tools;

/// <summary>
/// Ask how two things are connected, or what a change to one file can reach. Copied verbatim from the
/// standalone script <c>brain-path.mjs</c> (RESTRUCTURE.md slice 19, part 3): the five shapes it
/// dispatches on positional args (path-between, <c>--blast</c>, <c>--org</c>, <c>--patterns</c>,
/// <c>--clusters</c>/<c>--cluster</c>) are kept as one CLI-only tool, matching the single script it
/// came from. No MCP counterpart existed in <c>brain-path.mjs</c>, and none is added here. The instance
/// resolution and DB open that used to happen inline in the script (brain-path.mjs:10-19) belong to the
/// host once this tool is wired in a later slice, so <c>ExecuteCli</c> starts from an already-open
/// connection, same as every other tool in this project.
/// </summary>
public sealed partial class BrainPathTool : ITool
{
    public string Name => "brain-path";
    public string CliVerb => "brain-path";
    public string? McpName => null;
    public string Help =>
        "brain-path \"<from>\" \"<to>\"      shortest chain between two nodes\n" +
        "brain-path --blast <file>         what else carries this file's symbols\n" +
        "brain-path --org [filter]         the whole estate, read out of the store\n" +
        "brain-path --patterns             commands/procedures recorded often enough to codify\n" +
        "brain-path --clusters             every cluster in the graph\n" +
        "brain-path --cluster \"<node>\"     the cluster one node belongs to";

    public string ExecuteCli(SqliteConnection connection, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return Help;

        return args[0] switch
        {
            "--blast" => Blast(connection, args.Count > 1 ? args[1] : null),
            "--org" => Org(connection, args.Count > 1 ? args[1] : null),
            "--patterns" => Patterns(connection),
            "--clusters" => Clusters(connection),
            "--cluster" => Cluster(connection, args.Count > 1 ? args[1] : null),
            _ => PathBetween(connection, args),
        };
    }

    private static string Blast(SqliteConnection connection, string? file)
    {
        if (string.IsNullOrEmpty(file)) return "usage: brain-path.mjs --blast <file>";
        List<BrainGraphLib.BlastRow> rows = BrainGraphLib.BlastRadius(connection, file, 12);
        if (rows.Count == 0)
            return $"no shared symbols found for {file} — nothing else in the graph carries its declarations.";
        List<string> lines = [$"blast radius for {file}:"];
        foreach (BrainGraphLib.BlastRow r in rows)
            lines.Add($"  {r.Symbol.PadRight(34)} {r.Files} file(s), {r.Projects} project(s): {r.Names}");
        return string.Join("\n", lines);
    }

    private static string Org(SqliteConnection connection, string? rawFilter)
    {
        string? filter = string.IsNullOrEmpty(rawFilter) ? null : rawFilter.ToLowerInvariant();
        List<(string K, string Label, string Gloss)> orgs = [];
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT k, label, gloss FROM node WHERE valid_to IS NULL AND scheme='org' ORDER BY label";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) orgs.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
        }
        if (orgs.Count == 0) return "no organisations indexed — run index-org.mjs";

        List<string> lines = [];
        foreach ((string _, string label, string _) in orgs)
        {
            List<(string Label, string Gloss)> repos = [];
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT label, gloss FROM node WHERE valid_to IS NULL AND scheme='repo' AND label LIKE $p ORDER BY label";
                cmd.Parameters.AddWithValue("$p", $"{label}/%");
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read()) repos.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            }
            List<(string Label, string Gloss)> shown = filter is null
                ? repos
                : [.. repos.Where(r => $"{r.Label} {r.Gloss}".ToLowerInvariant().Contains(filter))];
            if (shown.Count == 0) continue;

            lines.Add("");
            lines.Add($"{label}  ({repos.Count} repos{(filter is not null ? $", {shown.Count} matching" : "")})");
            foreach ((string repoLabel, string gloss) in shown)
            {
                string name = repoLabel.Split('/').Last();
                string g = gloss;
                string lang = RepoLanguage().MatchOrEmpty(g) is { Success: true } lm ? lm.Groups[1].Value : "-";
                string vis = PrivateRepoMarker().IsMatchOrFalse(g) ? "private" : "public";
                string arch = ArchivedMarker().IsMatchOrFalse(g) ? "  [ARCHIVED]" : "";
                string fork = ForkMarker().IsMatchOrFalse(g) ? "  [fork]" : "";
                string pkg = PublishedNpmPackage().MatchOrEmpty(g) is { Success: true } pm ? pm.Groups[1].Value : "";
                string clone = g.Contains("cloned at") ? "cloned" : "";
                string desc = g.Split('.')[0];
                lines.Add($"  {name.PadRight(34)} {lang.PadRight(12)} {vis.PadRight(8)} {clone.PadRight(7)}{arch}{fork}");
                if (desc.Length > 0 && desc != "no description") lines.Add($"      {desc}");
                if (pkg.Length > 0) lines.Add($"      npm: {pkg}");
            }
        }
        return string.Join("\n", lines).TrimStart('\n');
    }

    private static string Patterns(SqliteConnection connection)
    {
        List<(string Sig, long Count, bool Promoted, string Kind)> rows = [];
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sig, count, promoted, COALESCE(kind,'command') AS kind, sample " +
                "FROM patterns ORDER BY (kind='sequence') DESC, count DESC LIMIT 30";
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
        foreach (string group in new[] { "sequence", "command" })
        {
            List<(string Sig, long Count, bool Promoted, string Kind)> of = [.. rows.Where(r => r.Kind == group)];
            if (of.Count == 0) continue;
            lines.Add("");
            lines.Add(group == "sequence" ? "procedures:" : "single commands:");
            foreach ((string sig, long count, bool promoted, string _) in of)
            {
                string flag = promoted ? " [settled]" : (count >= (group == "sequence" ? 3 : 5) ? " <- worth codifying" : "");
                lines.Add($"  {count,3}x  {sig}{flag}");
            }
        }
        return string.Join("\n", lines).TrimStart('\n');
    }

    private static string Clusters(SqliteConnection connection)
    {
        List<BrainGraphLib.Community> found = BrainGraphLib.Communities(connection);
        if (found.Count == 0) return "no clusters — the triple graph has no connected nodes yet.";
        List<string> lines = [$"{found.Count} cluster(s):", ""];
        foreach (BrainGraphLib.Community c in found.Take(12))
        {
            lines.Add($"  {c.Hub}  ({c.Size} nodes)");
            foreach (BrainGraphLib.CommunityMember m in c.Members.Skip(1).Take(5))
                lines.Add($"      {(m.Kind.Length > 0 ? $"[{m.Kind}] " : "")}{m.Label}");
            if (c.Size > 6) lines.Add($"      … {c.Size - 6} more");
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    private static string Cluster(SqliteConnection connection, string? name)
    {
        if (string.IsNullOrEmpty(name)) return "usage: brain-path.mjs --cluster \"<node>\"";
        BrainGraphLib.ResolvedNode? node = BrainGraphLib.ResolveNode(connection, name);
        if (node is null) return $"no node matches \"{name}\"";
        BrainGraphLib.Community? c = BrainGraphLib.CommunityOf(connection, node.K);
        if (c is null) return $"\"{node.Label}\" is not connected to anything else yet.";
        List<string> lines = [$"{node.Label} sits in the \"{c.Hub}\" cluster ({c.Size} nodes):", ""];
        foreach (BrainGraphLib.CommunityMember m in c.Members)
            lines.Add($"  {(m.Kind.Length > 0 ? $"[{m.Kind}] " : "")}{m.Label}");
        return string.Join("\n", lines);
    }

    private static string PathBetween(SqliteConnection connection, IReadOnlyList<string> args)
    {
        string? from = args.Count > 0 ? args[0] : null;
        string? to = args.Count > 1 ? args[1] : null;
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return new BrainPathTool().Help;

        BrainGraphLib.PathResult result = BrainGraphLib.PathBetween(connection, from, to);
        if (result.From is null) return $"no node matches \"{from}\"";
        if (result.To is null) return $"no node matches \"{to}\"";
        if (result.Hops is null)
            return $"no recorded connection between \"{result.From.Label}\" and \"{result.To.Label}\".";
        if (result.Hops.Count == 0) return "same node.";

        List<string> lines =
        [
            $"{result.From.Label}  ->  {result.To.Label}   ({result.Hops.Count} hop(s))"
        ];
        foreach (BrainGraphLib.Hop h in result.Hops)
        {
            string arrow = h.Dir == "out" ? "->" : "<-";
            lines.Add($"  {arrow} {h.Pred}  {h.To}{(h.Because.Length > 0 ? $"   ({h.Because})" : "")}");
        }
        return string.Join("\n", lines);
    }

    [GeneratedRegex("language ([A-Za-z+#]+)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex RepoLanguage();
    [GeneratedRegex(@"\. private\.", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PrivateRepoMarker();
    [GeneratedRegex("ARCHIVED", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ArchivedMarker();
    [GeneratedRegex(@"\. fork\.", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ForkMarker();
    [GeneratedRegex(@"publishes npm package (\S+?)\.", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PublishedNpmPackage();
}
