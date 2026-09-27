using Grimora.Store.Data;
using System.Text.Json;
using Grimora.Brain.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Grimora.Brain.Tools;

/// <summary>
/// Index the whole organisation, not just what happens to be cloned on this disk. Copied verbatim from
/// the standalone script <c>index-org.mjs</c> (RESTRUCTURE.md slice 19, part 3): the GitHub org listing
/// and local-clone match (index-org.mjs:30-66) are unchanged; the emitted fragment used to be replayed
/// by spawning <c>grimora.exe spine-import</c> as a separate process (index-org.mjs:121-124) — now that
/// <c>spine-import</c> is <see cref="SpineImportTool"/> in the same process, this tool calls it directly
/// instead of shelling back out to itself. CLI-only, no MCP counterpart existed in the script. The one
/// external call — <c>gh repo list</c>, and <c>git remote get-url</c> for the local-clone match — goes
/// through the injected <see cref="IProcessRunner"/> so a test never reaches the network or a real
/// <c>gh</c> installation.
/// </summary>
public sealed partial class BrainIndexOrgTool : ITool
{
    private const string Fields = "name,description,primaryLanguage,isPrivate,isArchived,isFork,defaultBranchRef,pushedAt,url,repositoryTopics";

    public string Name => "index-org";
    public string CliVerb => "index-org";
    public string? McpName => null;
    public string Help =>
        "index-org --orgs <a,b,c> [--clones <dir,dir>] [--dry] [--out <file>]   " +
        "reads GitHub org(s) via gh, matches local clones, writes a spine fragment and imports it.";

    public string ExecuteCli(
        SqliteConnection connection,
        IReadOnlyList<string> orgs,
        IReadOnlyList<string> searchRoots,
        string outPath,
        bool dry,
        IProcessRunner runner)
    {
        List<string> lines = [$"indexing {orgs.Count} org(s)"];

        Dictionary<string, string> clones = LocalClones(searchRoots, runner);
        lines.Add($"  found {clones.Count} local clone(s) under {string.Join(", ", searchRoots)}");

        List<object> nodes = [];
        List<object> links = [];
        int total = 0, archived = 0, forks = 0, cloned = 0;

        foreach (string org in orgs)
        {
            string orgKey = $"org:{org}";
            nodes.Add(new { k = orgKey, kind = "platform", label = org, gloss = $"GitHub organisation {org}", scheme = "org", hard = 0 });

            List<JsonElement> repos = ReposOf(org, runner);
            lines.Add($"  {org}: {repos.Count} repo(s)");
            foreach (JsonElement r in repos)
            {
                total++;
                bool isArchived = Bool(r, "isArchived");
                bool isFork = Bool(r, "isFork");
                if (isArchived) archived++;
                if (isFork) forks++;
                string name = Str(r, "name");
                string key = $"repo:{org}/{name}";
                string lang = r.TryGetProperty("primaryLanguage", out JsonElement pl) && pl.ValueKind == JsonValueKind.Object
                    ? Str(pl, "name") : "";
                List<string> topics = [];
                if (r.TryGetProperty("repositoryTopics", out JsonElement topEl) && topEl.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement t in topEl.EnumerateArray())
                    {
                        string tn = Str(t, "name");
                        if (tn.Length == 0 && t.TryGetProperty("topic", out JsonElement tp)) tn = Str(tp, "name");
                        if (tn.Length > 0) topics.Add(tn);
                    }
                string url = Str(r, "url");
                string? local = clones.GetValueOrDefault(url.ToLowerInvariant());
                if (local is not null) cloned++;
                string? pkg = local is not null ? PackageName(local) : null;

                List<string> facts =
                [
                    Str(r, "description") is { Length: > 0 } d ? d : "no description",
                    lang.Length > 0 ? $"language {lang}" : "",
                    Bool(r, "isPrivate") ? "private" : "public",
                    isArchived ? "ARCHIVED" : "",
                    isFork ? "fork" : "",
                    $"default branch {(r.TryGetProperty("defaultBranchRef", out JsonElement db) && db.ValueKind == JsonValueKind.Object ? Str(db, "name") : "unknown")}",
                    Str(r, "pushedAt") is { Length: >= 10 } pa ? $"last pushed {pa[..10]}" : "",
                    local is not null ? $"cloned at {local}" : "not cloned locally",
                    pkg is not null ? $"publishes npm package {pkg}" : "",
                    topics.Count > 0 ? $"topics: {string.Join(", ", topics)}" : "",
                    url,
                ];
                string gloss = string.Join(". ", facts.Where(f => f.Length > 0));

                nodes.Add(new { k = key, kind = "project", label = $"{org}/{name}", gloss, scheme = "repo", hard = 0 });
                links.Add(new { s = key, p = "part_of", o = orgKey, because = "" });
            }
        }

        object fragment = new { nodes, slots = Array.Empty<object>(), links, aliases = Array.Empty<object>(), terms = Array.Empty<object>(), edges = Array.Empty<object>() };
        File.WriteAllText(outPath, JsonSerializer.Serialize(fragment, new JsonSerializerOptions { WriteIndented = true }));
        lines.Add("");
        lines.Add($"{total} repo(s): {cloned} cloned, {archived} archived, {forks} fork(s)");
        lines.Add($"wrote {nodes.Count} node(s) + {links.Count} link(s) to {outPath}");

        if (dry)
        {
            lines.Add("(dry run — not imported)");
            return string.Join("\n", lines);
        }

        string imported = new SpineImportTool().ExecuteCli(connection, outPath);
        lines.Add(imported.Trim().Split('\n').Last());
        return string.Join("\n", lines);
    }

    private static List<JsonElement> ReposOf(string org, IProcessRunner runner)
    {
        (string stdout, _, int exitCode) = runner.Run("gh", ["repo", "list", org, "--limit", "500", "--json", Fields]);
        if (exitCode != 0) return [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdout);
            return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Matched by remote URL rather than folder name — index-org.mjs:38-58.
    private static Dictionary<string, string> LocalClones(IReadOnlyList<string> searchRoots, IProcessRunner runner)
    {
        Dictionary<string, string> byRemote = [];
        void Visit(string dir, int depth)
        {
            if (depth > 3) return;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch { return; }
            List<string> entryList = [.. entries];
            if (entryList.Any(e => Path.GetFileName(e) == ".git"))
            {
                (string stdout, string _, int exitCode) = runner.Run("git", ["-C", dir, "remote", "get-url", "origin"]);
                if (exitCode == 0)
                {
                    string url = RemoteUrlGitSuffix().ReplaceOrKeep(stdout.Trim(), "");
                    url = GithubSshPrefix().ReplaceOrKeep(url, "https://github.com/");
                    if (url.Length > 0) byRemote[url.ToLowerInvariant()] = dir.Replace('\\', '/');
                }
            }
            foreach (string e in entryList)
            {
                if (!Directory.Exists(e)) continue;
                string name = Path.GetFileName(e);
                if (name == "node_modules" || name == ".git" || name.StartsWith('.')) continue;
                Visit(e, depth + 1);
            }
        }
        foreach (string root in searchRoots) Visit(root, 0);
        return byRemote;
    }

    private static string? PackageName(string dir)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "package.json")));
            JsonElement root = doc.RootElement;
            bool priv = root.TryGetProperty("private", out JsonElement p) && p.ValueKind == JsonValueKind.True;
            return root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String && !priv
                ? n.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"\.git$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex RemoteUrlGitSuffix();
    [GeneratedRegex("^git@github\\.com:", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex GithubSshPrefix();
}
