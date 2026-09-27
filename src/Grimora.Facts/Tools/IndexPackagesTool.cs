using System.Text.Json;
using Grimora.Facts.Data;
using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Facts.Tools;

/// <summary>
/// Commits one fact per <c>package.json</c> under a root. Copied verbatim from grimora.cs's
/// <c>IndexPackages</c> (grimora.cs:799) and the <c>EnumerateSource</c> helper it walks with (grimora.cs:2880).
///
/// RESTRUCTURE.md section 2.1 (corrected by slice 11b): this tool writes <c>facts</c>
/// (<c>UpsertFact</c>, grimora.cs:821), so it belongs in Grimora.Facts, not Grimora.Graph — Graph may not touch
/// Facts' tables (section 1, <c>ReferenceDirectionTests</c>).
/// </summary>
public sealed class IndexPackagesTool : ITool
{
    public string Name => "index-packages";
    public string CliVerb => "index-packages";
    public string? McpName => null;
    public string Help => "index-packages [--root <dir>]       index package.json identities";

    public string Execute(SqliteConnection connection, string root)
    {
        if (!Directory.Exists(root)) return $"root not found: {root}";
        int n = 0;
        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        foreach (string file in EnumerateSource(root, ["package.json"]))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch { continue; }
            using (doc)
            {
                JsonElement r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("name", out JsonElement nameEl)) continue;
                string? name = nameEl.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                string version = r.TryGetProperty("version", out JsonElement v) ? v.GetString() ?? "" : "";
                string desc = r.TryGetProperty("description", out JsonElement d) ? d.GetString() ?? "" : "";
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                string dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? ".";
                string value = $"{name} v{version}" + (desc.Length > 0 ? $" — {desc}" : "") + $". Package at {dir}/.";
                UpsertFact(connection, name, name, "[]", "package", value, rel, "", "index-packages");
                n++;
            }
        }
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }
        return $"indexed {n} package(s) from {root}.";
    }

    private static void UpsertFact(SqliteConnection connection, string k, string term, string aliases,
        string category, string value, string source, string notes, string why)
    {
        string? before = FactRecord.ReadJson(connection, k);
        Run(connection, "INSERT INTO facts(k,term,aliases,category,value,source,notes) VALUES($k,$t,$al,$c,$v,$s,$n) " +
            "ON CONFLICT(k) DO UPDATE SET term=$t,aliases=$al,category=$c,value=$v,source=$s,notes=$n",
            ("$k", k), ("$t", term), ("$al", aliases), ("$c", category), ("$v", value), ("$s", source), ("$n", notes));
        Run(connection, "DELETE FROM facts_fts WHERE k=$k", ("$k", k));
        Run(connection, "INSERT INTO facts_fts(k,term,aliases,category,value,notes) VALUES($k,$t,$al,$c,$v,$n)",
            ("$k", k), ("$t", term), ("$al", aliases), ("$c", category), ("$v", value), ("$n", notes));
        string after = FactRecord.ReadJson(connection, k)!;
        if (before != after)
            MutationLog.Append(connection, "fact", k, before is null ? "insert" : "update", before, after, why);
    }

    private static void Run(SqliteConnection connection, string sql, params (string name, string value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, string value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    // Prune heavy dirs DURING the walk (never descend into node_modules); tolerate unreadable dirs.
    private static IEnumerable<string> EnumerateSource(string root, string[] patterns)
    {
        // .scratch and friends hold staged COPIES of real packages. Indexed, a staging copy wins the
        // upsert and the store then reports a package's home as the scratch directory it was last built
        // into.
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
}
