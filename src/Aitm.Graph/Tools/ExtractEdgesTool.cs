using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Aitm.Graph.Tools;

/// <summary>
/// Heuristic edge extraction: scans every registered project's source for a changing symbol and stages
/// the hits as candidates (reviewed before promotion, so grep noise never reaches <c>impact</c>). Copied
/// verbatim from aitm.cs's <c>ExtractEdges</c> and its helpers <c>EnumerateSource</c>,
/// <c>ContainsToken</c>, <c>IsIdent</c>, <c>LeanUsage</c>, <c>IsHardcoded</c> (aitm.cs:2830-2948).
/// </summary>
public sealed partial class ExtractEdgesTool : ITool
{
    public string Name => "extract-edges";
    public string CliVerb => "extract-edges";
    public string? McpName => null;
    public string Help =>
        "extract-edges --symbol <s> [--contract <c>]  scan registered projects for consumers of a symbol";

    public string Execute(SqliteConnection connection, string symbol, string contract)
    {
        List<(string name, string root, string globs)> projects = new();
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT name,root,globs FROM projects ORDER BY name";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) projects.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        if (projects.Count == 0)
            return "no projects registered — add one: aitm project --name web --root <path> --globs \"*.ts,*.vue\"";

        System.Text.StringBuilder skipped = new();
        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        using (SqliteCommand del = connection.CreateCommand())
        {
            del.CommandText = "DELETE FROM edge_candidates WHERE symbol=$s AND status='pending'";
            del.Parameters.AddWithValue("$s", symbol);
            del.ExecuteNonQuery();
        }
        int found = 0;
        foreach ((string name, string root, string globs) in projects)
        {
            if (!Directory.Exists(root)) { skipped.AppendLine($"  {name}: root not found ({root}) — skipped"); continue; }
            string[] patterns = globs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string file in EnumerateSource(root, patterns))
            {
                int lineNo = 0, repLine = 0;
                string? repUsage = null;
                bool anyHard = false, any = false;
                foreach (string line in File.ReadLines(file))
                {
                    lineNo++;
                    if (!ContainsToken(line, symbol)) continue;
                    bool hard = IsHardcoded(line, symbol);
                    if (!any || (hard && !anyHard))
                    {
                        repLine = lineNo;
                        repUsage = LeanUsage(line, symbol);
                    }
                    anyHard |= hard;
                    any = true;
                }
                if (!any) continue;
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                using SqliteCommand insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO edge_candidates(symbol,contract,project,file,line,usage,hardcoded,status) VALUES($s,$c,$p,$f,$l,$u,$h,'pending')";
                insert.Parameters.AddWithValue("$s", symbol);
                insert.Parameters.AddWithValue("$c", contract);
                insert.Parameters.AddWithValue("$p", name);
                insert.Parameters.AddWithValue("$f", rel);
                insert.Parameters.AddWithValue("$l", repLine);
                insert.Parameters.AddWithValue("$u", (object?)repUsage ?? DBNull.Value);
                insert.Parameters.AddWithValue("$h", anyHard ? 1 : 0);
                insert.ExecuteNonQuery();
                found++;
            }
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        return $"{skipped}extracted {found} candidate consumer(s) of '{symbol}'. Review: aitm candidates --symbol {symbol}   then: aitm promote <id>";
    }

    // Prune heavy dirs DURING the walk (never descend into node_modules); tolerate unreadable dirs.
    private static IEnumerable<string> EnumerateSource(string root, string[] patterns)
    {
        string[] skip =
        {
            "node_modules", "dist", "build", "bin", "obj", ".git", ".nuxt", ".gradle", "vendor", ".idea", ".vs",
            ".scratch", ".turbo", ".next", ".output", ".svelte-kit", "coverage", "out", "target", "__pycache__",
        };
        Stack<string> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { subs = Array.Empty<string>(); }
            foreach (string sub in subs)
                if (!skip.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
            foreach (string pattern in patterns)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, pattern); }
                catch { files = Array.Empty<string>(); }
                foreach (string file in files) yield return file;
            }
        }
    }

    // Whole-token match: the symbol must not be flanked by identifier chars, so "data" never matches "metadata".
    private static bool ContainsToken(string line, string symbol)
    {
        int i = 0;
        while ((i = line.IndexOf(symbol, i, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = i == 0 || !IsIdent(line[i - 1]);
            int end = i + symbol.Length;
            bool rightOk = end >= line.Length || !IsIdent(line[end]);
            if (leftOk && rightOk) return true;
            i = end;
        }
        return false;
    }

    private static bool IsIdent(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    // Store the code line only when it shows non-obvious USAGE. A bare declaration/annotation of the symbol
    // ([JsonProperty("device_id")], @SerialName("device_id"), device_id: string) just echoes the symbol and is
    // identical across many files — return "" so impact() shows file:line without the repeated boilerplate.
    private static string LeanUsage(string line, string symbol)
    {
        string trimmed = line.Trim();
        if (trimmed.Length > 120) trimmed = trimmed[..120];
        string camel = MyRegex().Replace(symbol, m => m.Groups[1].Value.ToUpperInvariant());
        string residue = MyRegex1().Replace(trimmed,
            "");
        residue = System.Text.RegularExpressions.Regex.Replace(residue, System.Text.RegularExpressions.Regex.Escape(symbol), "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        residue = System.Text.RegularExpressions.Regex.Replace(residue, System.Text.RegularExpressions.Regex.Escape(camel), "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        residue = MyRegex2().Replace(residue, "");
        return residue.Length <= 1 ? "" : trimmed; // nothing left but the symbol + decl syntax -> declaration, no usage
    }

    // Consumed by NAME (the break-on-rename case): symbol in quotes, via @JsonProperty, or a property access.
    private static bool IsHardcoded(string line, string symbol) =>
        line.Contains($"\"{symbol}\"", StringComparison.Ordinal) ||
        line.Contains($"'{symbol}'", StringComparison.Ordinal) ||
        line.Contains($"@JsonProperty({symbol}", StringComparison.Ordinal) ||
        line.Contains($".{symbol}", StringComparison.Ordinal);

    [GeneratedRegex(@"_(\w)")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"@?\[?\b(JsonProperty|JsonPropertyName|SerialName|JsonInclude|DataMember|field|get|set|init|public|private|internal|val|var|let|const|readonly|required|override|string|String|int|Int|long|Long|bool|Boolean|number|Guid|Ulid)\b|[\[\]@(){}<>"":;,?=]", RegexOptions.IgnoreCase, "nl-NL")]
    private static partial Regex MyRegex1();
    [GeneratedRegex(@"\s+")]
    private static partial Regex MyRegex2();
}
