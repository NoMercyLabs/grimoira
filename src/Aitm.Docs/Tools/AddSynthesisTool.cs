using Microsoft.Data.Sqlite;
using Aitm.Store.Tools;

namespace Aitm.Docs.Tools;

/// <summary>
/// Files an answer distilled from a source set — a synthesis, stamped verbatim with its sources and
/// their newest mtime so a reader can tell when the sources have moved on underneath it. Copied verbatim
/// from aitm.cs's <c>AddSynthesis</c> (aitm.cs:1063).
/// </summary>
public sealed class AddSynthesisTool : ITool
{
    public string Name => "add-synthesis";
    public string CliVerb => "add-synthesis";
    public string? McpName => null;
    public string Help =>
        "add-synthesis --path <dir> --from <f>  file an answer distilled from a source set";

    public string Execute(SqliteConnection connection, string sourceDir, string bodyFile, string title, string sources)
    {
        string body;
        try { body = File.ReadAllText(bodyFile); }
        catch (Exception e) { return $"add-synthesis: cannot read {bodyFile} ({e.Message})"; }
        if (body.Trim().Length < 400) return "add-synthesis: body too short to be worth storing.";

        string dir = Path.GetFullPath(sourceDir).Replace('\\', '/').TrimEnd('/');
        string key = $"synthesis:{dir.ToLowerInvariant()}";
        string name = string.IsNullOrWhiteSpace(title) ? dir.Split('/').Last().Replace('-', ' ') : title;

        long newest = 0;
        foreach (string s in sources.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { newest = Math.Max(newest, new FileInfo(s.Trim()).LastWriteTimeUtc.Ticks); }
            catch { /* source moved or renamed */ }
        }

        string names = string.Join(", ", sources.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => Path.GetFileName(s.Trim())));
        string content = body.Replace("\r\n", "\n").Trim();
        string stamped = $"synthesis of {dir}\nfrom: {names}\nnewest_source_ticks: {newest}\n\n{content}";
        string terms = $"{PathTerms(dir)} synthesis overview brief";

        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        using (SqliteCommand upsert = connection.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO docs(k,path,title,category,content,terms) VALUES($k,$p,$t,'synthesis',$co,$te)
                ON CONFLICT(k) DO UPDATE SET title=$t,content=$co,terms=$te
                """;
            upsert.Parameters.AddWithValue("$k", key);
            upsert.Parameters.AddWithValue("$p", dir);
            upsert.Parameters.AddWithValue("$t", name);
            upsert.Parameters.AddWithValue("$co", stamped);
            upsert.Parameters.AddWithValue("$te", terms);
            upsert.ExecuteNonQuery();
        }
        using (SqliteCommand del = connection.CreateCommand())
        {
            del.CommandText = "DELETE FROM docs_fts WHERE k=$k";
            del.Parameters.AddWithValue("$k", key);
            del.ExecuteNonQuery();
        }
        using (SqliteCommand ins = connection.CreateCommand())
        {
            ins.CommandText = "INSERT INTO docs_fts(k,title,content) VALUES($k,$t,$co)";
            ins.Parameters.AddWithValue("$k", key);
            ins.Parameters.AddWithValue("$t", $"{name} {terms}");
            ins.Parameters.AddWithValue("$co", stamped);
            ins.ExecuteNonQuery();
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }

        return $"synthesis stored for {dir} ({content.Length} chars).";
    }

    private static string PathTerms(string fullPath)
    {
        string[] generic =
        {
            "src", "content", "site", "docs", "doc", "md", "readme", "index", "app", "apps", "packages",
            "projects", "c", "entries", "reports", "claude", "work", "public", "assets", "pages",
        };
        IEnumerable<string> words = System.Text.RegularExpressions.Regex.Split(fullPath, @"[\\/\-_. ]+")
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 1 && !w.All(char.IsDigit) && !generic.Contains(w));
        return string.Join(' ', words.Distinct());
    }
}
