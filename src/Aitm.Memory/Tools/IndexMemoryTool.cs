using System.Text;
using System.Text.RegularExpressions;
using Aitm.Store.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Memory.Tools;

/// <summary>
/// Migrates the always-loaded MEMORY.md files into the pull-based memory channel. Copied verbatim from
/// aitm.cs's <c>IndexMemory</c> (aitm.cs:1207), <c>ParseFrontmatter</c> (aitm.cs:1230), <c>UpsertMemory</c>
/// (aitm.cs:1249), <c>Compact</c> (aitm.cs:913) and <c>StripFiller</c> (aitm.cs:935).
/// </summary>
public sealed class IndexMemoryTool : ITool
{
    public string Name => "index-memory";
    public string CliVerb => "index-memory";
    public string? McpName => null;
    public string Help => "index-memory --from <memory dir>      migrate MEMORY.md-style files into the memory channel";

    public string Execute(SqliteConnection connection, string dir)
    {
        if (!Directory.Exists(dir)) return $"memory dir not found: {dir}";

        int n = 0, hardN = 0;
        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        foreach (string file in Directory.GetFiles(dir, "*.md"))
        {
            string slug = Path.GetFileNameWithoutExtension(file);
            if (slug.Equals("MEMORY", StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            (Dictionary<string, string> fm, string rawBody) = ParseFrontmatter(text);
            string type = fm.GetValueOrDefault("type", "feedback");
            string title = fm.GetValueOrDefault("name", slug);
            string hook = Compact(fm.GetValueOrDefault("description", ""));
            string body = Compact(rawBody);
            if (hook.Length == 0) hook = body.Length <= 200 ? body : body[..200];
            string links = string.Join(",", Regex.Matches(rawBody, @"\[\[([^\]]+)\]\]").Select(m => m.Groups[1].Value).Distinct());
            bool hard = (title + " " + hook).Contains("hard rule", StringComparison.OrdinalIgnoreCase);
            if (hard) hardN++;
            UpsertMemory(connection, slug, type, title, hook, body, links, hard, "index-memory");
            n++;
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        return $"indexed {n} memor(ies) ({hardN} hard rule(s)).";
    }

    private static void UpsertMemory(SqliteConnection connection, string k, string type, string title,
        string hook, string body, string links, bool hard, string why)
    {
        string? before = ScalarText(connection, "SELECT type||'|'||title||'|'||hook||'|'||body FROM memory WHERE k=$k", k);

        using (SqliteCommand upsert = connection.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO memory(k,type,title,hook,body,links,hard) VALUES($k,$ty,$ti,$h,$b,$l,$hd)
                ON CONFLICT(k) DO UPDATE SET type=$ty,title=$ti,hook=$h,body=$b,links=$l,hard=$hd
                """;
            upsert.Parameters.AddWithValue("$k", k);
            upsert.Parameters.AddWithValue("$ty", type);
            upsert.Parameters.AddWithValue("$ti", title);
            upsert.Parameters.AddWithValue("$h", hook);
            upsert.Parameters.AddWithValue("$b", body);
            upsert.Parameters.AddWithValue("$l", links);
            upsert.Parameters.AddWithValue("$hd", hard ? 1 : 0);
            upsert.ExecuteNonQuery();
        }
        using (SqliteCommand del = connection.CreateCommand())
        {
            del.CommandText = "DELETE FROM memory_fts WHERE k=$k";
            del.Parameters.AddWithValue("$k", k);
            del.ExecuteNonQuery();
        }
        using (SqliteCommand ins = connection.CreateCommand())
        {
            ins.CommandText = "INSERT INTO memory_fts(k,title,hook,body) VALUES($k,$ti,$h,$b)";
            ins.Parameters.AddWithValue("$k", k);
            ins.Parameters.AddWithValue("$ti", title);
            ins.Parameters.AddWithValue("$h", hook);
            ins.Parameters.AddWithValue("$b", body);
            ins.ExecuteNonQuery();
        }
        string after = $"{type}|{title}|{hook}|{body}";
        if (before != after) MutationLog.Append(connection, "memory", k, before is null ? "insert" : "update", before, after, why);
    }

    private static string? ScalarText(SqliteConnection connection, string sql, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    // Split YAML-ish frontmatter from body. Tolerant: first non-empty value per key wins, so a nested
    // `metadata:\n  type: x` still yields type=x while a top-level `type: x` yields the same.
    private static (Dictionary<string, string> fm, string body) ParseFrontmatter(string text)
    {
        text = text.Replace("\r\n", "\n");
        Dictionary<string, string> fm = new(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (fm, text.Trim());
        int end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (fm, text.Trim());
        foreach (string line in text[4..end].Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim();
            string val = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (val.Length > 0 && !fm.ContainsKey(key)) fm[key] = val;
        }
        string body = text[(end + 4)..].TrimStart('\n', '-', ' ').Trim();
        return (fm, body);
    }

    // Densify text for cheap re-injection: strip markdown chrome (headings, emphasis, list/quote markers,
    // code fences, table pipes, link URLs, image/badge lines, separators) and collapse whitespace.
    private static string Compact(string text)
    {
        StringBuilder sb = new();
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("![") || line.StartsWith("```")) continue;
            if (line.All(ch => ch is '-' or '=' or '*' or '_' or '|' or ' ' or ':')) continue;
            line = line.TrimStart('#', '>', '-', '*', '+', ' ', '\t');
            if (line.StartsWith("[ ] ")) line = line[4..];
            else if (line.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) line = line[4..];
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "").Replace("|", " ");
            line = Regex.Replace(line, @"\s{2,}", " ").Trim();
            if (line.Length > 0) sb.Append(line).Append('\n');
        }
        return StripFiller(sb.ToString().Trim());
    }

    // Drop human-speech filler that carries no technical meaning: verbose phrases -> concise, qualifier
    // words removed, emoji stripped. Articles are NOT touched (too close to identifiers).
    private static string StripFiller(string text)
    {
        (string from, string to)[] phrases =
        {
            ("in order to", "to"), ("due to the fact that", "because"), ("in the event that", "if"),
            ("for the purpose of", "for"), ("a large number of", "many"), ("in close proximity to", "near"),
            ("at this point in time", "now"), ("it is important to note that", "note:"),
            ("with the exception of", "except"), ("in spite of the fact that", "although"),
            ("on account of the fact that", "because"), ("has the ability to", "can"),
            ("is able to", "can"), ("a number of", "several"), ("the majority of", "most"),
        };
        foreach ((string from, string to) in phrases)
            text = Regex.Replace(text, Regex.Escape(from), to, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(very|really|just|actually|basically|simply|essentially|quite|somewhat|fairly|definitely|absolutely|literally|obviously|clearly|please|kindly)\b ?", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\p{Cs}", "");
        return Regex.Replace(text, @"[ \t]{2,}", " ");
    }
}
