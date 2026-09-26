using System.Text;
using System.Text.RegularExpressions;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Docs.Tools;

/// <summary>
/// Absorbs AI-meta docs into the docs channel, chunked by markdown heading so recall returns the
/// relevant section. Sheds outdated text: completed-checklist tracking, changelogs, and done/superseded
/// sections. Copied verbatim from aitm.cs's <c>IndexDocs</c> (aitm.cs:831), <c>PathTerms</c> (aitm.cs:876),
/// <c>CanonicalCopies</c> (aitm.cs:892), <c>Compact</c> (aitm.cs:913), <c>StripFiller</c> (aitm.cs:935),
/// <c>IsOutdated</c> (aitm.cs:984), <c>ChunkMarkdown</c> (aitm.cs:997) and <c>EnumerateSource</c> (aitm.cs:2880).
/// </summary>
public sealed partial class IndexDocsTool : ITool
{
    public string Name => "index-docs";
    public string CliVerb => "index-docs";
    public string? McpName => null;
    public string Help =>
        "index-docs --from <dir|file.md> [--category <c>]   absorb AI-meta docs, chunked by section (default category 'doc')";

    public string Execute(SqliteConnection connection, string fromPath, string category)
    {
        IEnumerable<string> found = Directory.Exists(fromPath) ? EnumerateSource(fromPath, ["*.md"]) : new[] { fromPath };
        List<string> files = CanonicalCopies(found);
        int docCount = 0, chunkCount = 0, shedCount = 0;

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        foreach (string file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            string rel = Path.GetFullPath(file).Replace('\\', '/');
            string key = rel.ToLowerInvariant();
            string terms = PathTerms(rel);
            bool any = false;
            foreach ((string title, string body, int idx) in ChunkMarkdown(text))
            {
                if (IsOutdated(title, body)) { shedCount++; continue; }
                string content = Compact(title + "\n" + body);
                if (content.Length < 24) continue;
                string k = $"{key}#{idx}";

                using (SqliteCommand upsert = connection.CreateCommand())
                {
                    upsert.CommandText = """
                        INSERT INTO docs(k,path,title,category,content,terms) VALUES($k,$p,$t,$c,$co,$te)
                        ON CONFLICT(k) DO UPDATE SET title=$t,category=$c,content=$co,terms=$te
                        """;
                    upsert.Parameters.AddWithValue("$k", k);
                    upsert.Parameters.AddWithValue("$p", rel);
                    upsert.Parameters.AddWithValue("$t", title);
                    upsert.Parameters.AddWithValue("$c", category);
                    upsert.Parameters.AddWithValue("$co", content);
                    upsert.Parameters.AddWithValue("$te", terms);
                    upsert.ExecuteNonQuery();
                }
                using (SqliteCommand del = connection.CreateCommand())
                {
                    del.CommandText = "DELETE FROM docs_fts WHERE k=$k";
                    del.Parameters.AddWithValue("$k", k);
                    del.ExecuteNonQuery();
                }
                using (SqliteCommand ins = connection.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO docs_fts(k,title,content) VALUES($k,$t,$co)";
                    ins.Parameters.AddWithValue("$k", k);
                    ins.Parameters.AddWithValue("$t", $"{title} {terms}");
                    ins.Parameters.AddWithValue("$co", content);
                    ins.ExecuteNonQuery();
                }
                chunkCount++;
                any = true;
            }
            if (any) docCount++;
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        return $"indexed {docCount} doc(s) -> {chunkCount} section(s) [{category}]; shed {shedCount} outdated section(s).";
    }

    private static string PathTerms(string fullPath)
    {
        string[] generic =
        [
            "src", "content", "site", "docs", "doc", "md", "readme", "index", "app", "apps", "packages",
            "projects", "c", "entries", "reports", "claude", "work", "public", "assets", "pages",
        ];
        IEnumerable<string> words = PathWordSeparators().Split(fullPath)
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 1 && !w.All(char.IsDigit) && !generic.Contains(w));
        return string.Join(' ', words.Distinct());
    }

    private static List<string> CanonicalCopies(IEnumerable<string> files)
    {
        Dictionary<string, string> byHash = [];
        List<string> ordered = [];
        foreach (string file in files)
        {
            string hash;
            try { hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))); }
            catch { continue; }
            string norm = Path.GetFullPath(file).Replace('\\', '/');
            if (!byHash.TryGetValue(hash, out string? held)) { byHash[hash] = norm; ordered.Add(hash); continue; }
            if (norm.Length < held.Length) byHash[hash] = norm;
        }
        return [.. ordered.Select(h => byHash[h])];
    }

    internal static string Compact(string text)
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
            line = MarkdownLink().Replace(line, "$1");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "").Replace("|", " ");
            line = ConsecutiveWhitespace().Replace(line, " ").Trim();
            if (line.Length > 0) sb.Append(line).Append('\n');
        }
        return StripFiller(sb.ToString().Trim());
    }

    private static string StripFiller(string text)
    {
        (string from, string to)[] phrases =
        [
            ("in order to", "to"), ("due to the fact that", "because"), ("in the event that", "if"),
            ("for the purpose of", "for"), ("a large number of", "many"), ("in close proximity to", "near"),
            ("at this point in time", "now"), ("it is important to note that", "note:"),
            ("with the exception of", "except"), ("in spite of the fact that", "although"),
            ("on account of the fact that", "because"), ("has the ability to", "can"),
            ("is able to", "can"), ("a number of", "several"), ("the majority of", "most"),
        ];
        foreach ((string from, string to) in phrases)
            text = Regex.Replace(text, Regex.Escape(from), to, RegexOptions.IgnoreCase);
        text = FillerWords().Replace(text, "");
        text = Surrogates().Replace(text, "");
        return RepeatedSpacesAndTabs().Replace(text, " ");
    }

    private static bool IsOutdated(string title, string body)
    {
        string t = title.ToLowerInvariant();
        if (t.Contains("changelog") || t.Contains("superseded") || t.Contains("obsolete") || t.Contains("revision history") || t.Contains("completed") || t.Contains("✅"))
            return true;
        string[] lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length >= 4)
        {
            int done = lines.Count(l => l.TrimStart().StartsWith("- [x]", StringComparison.OrdinalIgnoreCase));
            if (done * 2 >= lines.Length) return true;
        }
        return false;
    }

    private static IEnumerable<(string title, string body, int idx)> ChunkMarkdown(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        string title = "(intro)";
        StringBuilder body = new();
        int idx = 0;
        foreach (string line in lines)
        {
            if (line.StartsWith('#'))
            {
                if (body.Length > 0) { yield return (title, body.ToString(), idx++); body.Clear(); }
                title = line.TrimStart('#', ' ').Trim();
            }
            else body.AppendLine(line);
        }
        if (body.Length > 0) yield return (title, body.ToString(), idx);
    }

    private static IEnumerable<string> EnumerateSource(string root, string[] patterns)
    {
        string[] skip =
        [
            "node_modules", "dist", "build", "bin", "obj", ".git", ".nuxt", ".gradle", "vendor", ".idea", ".vs",
            ".scratch", ".turbo", ".next", ".output", ".svelte-kit", "coverage", "out", "target", "__pycache__",
        ];
        Stack<string> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { subs = []; }
            foreach (string sub in subs)
                if (!skip.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
            foreach (string pattern in patterns)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, pattern); }
                catch { files = []; }
                foreach (string file in files) yield return file;
            }
        }
    }

    [GeneratedRegex(@"[\\/\-_. ]+")]
    private static partial Regex PathWordSeparators();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLink();
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex ConsecutiveWhitespace();
    [GeneratedRegex(@"\b(very|really|just|actually|basically|simply|essentially|quite|somewhat|fairly|definitely|absolutely|literally|obviously|clearly|please|kindly)\b ?", RegexOptions.IgnoreCase, "nl-NL")]
    private static partial Regex FillerWords();
    [GeneratedRegex(@"\p{Cs}")]
    private static partial Regex Surrogates();
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex RepeatedSpacesAndTabs();
}
