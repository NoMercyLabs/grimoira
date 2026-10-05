using Grimoira.Store.Data;
using System.Text.RegularExpressions;
using Grimoira.Store.Schema;
using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Graph.Tools;

/// <summary>
/// Bulk code indexer: fills <c>edges</c> with the PUBLIC DECLARATION surface of every registered
/// project, so the brain can answer "what is X / where does X live" without a tree scan. Declarations
/// only, not usages — usages explode the table and the exported surface is what questions are about.
/// Copied verbatim (rules, skip list, walk order, idempotency) from <c>index-code.mjs</c>
/// (RESTRUCTURE.md slice 14).
///
/// Idempotent: an existing (symbol,file,line) row is left alone, so this can run on every session end.
/// A re-index REPLACES a project's own declaration rows (2026-10-05): a row this tool wrote earlier for a
/// file that is gone, or for a (symbol,line) the re-scan no longer finds, is deleted. Before this, rows
/// only ever accumulated and 51% of the live NoMercy store pointed at files that no longer existed after
/// a workspace move. Only rows carrying this tool's own signature (<c>contract='decl'</c>, usage
/// "<c>&lt;lang&gt; declaration</c>") for the scanned project are touched; curated (seed-edges) rows and
/// other projects' rows are never deleted. Paths are stored through <see cref="NormalizePath"/> so one
/// file has one path (the live store held <c>c:/...</c> and <c>C:/...</c> rows for the same file).
/// Applies <see cref="Schema.GraphIndexSchema"/> through <see cref="SchemaRunner.Run"/> first (Graph
/// schema step 1), same as the two <c>CREATE INDEX IF NOT EXISTS</c> statements index-code.mjs runs
/// before it queries <c>edges</c>.
/// </summary>
public sealed partial class IndexCodeTool : ITool
{
    public string Name => "index-code";
    public string CliVerb => "index-code";
    public string? McpName => null;
    public string Help =>
        "index-code [--project <name>]  bulk-index every registered project's declaration surface (public and internal) into edges; a re-index drops rows for files that are gone";

    private static readonly HashSet<string> SkipDir = new(StringComparer.Ordinal)
    {
        "node_modules", ".git", "dist", "build", "bin", "obj", ".next", ".nuxt", ".gradle", ".idea",
        "vendor", "coverage", ".venv", "venv", "__pycache__", "Pods", "DerivedData", ".svelte-kit",
        "wwwroot", "publish", "artifacts", ".turbo", "out", "target", ".cache", "worktrees",
        "third_party", "thirdparty", "deps", "external", "subprojects", "submodules", "extern",
    };


    private const long MaxBytes = 1_500_000;

    private static readonly Dictionary<string, Regex[]> Rules = new()
    {
        ["ts"] =
        [
            TypeScriptExportedClass(),
            TypeScriptExportedFunction(),
            TypeScriptExportedTypeDeclaration(),
            TypeScriptExportedVariable(),
            TypeScriptExportList(),
        ],
        ["kotlin"] =
        [
            KotlinTypeDeclaration(),
            KotlinFunction(),
            KotlinConstant(),
        ],
        ["csharp"] =
        [
            CSharpPublicType(),
            CSharpPublicMethod(),
        ],
        ["php"] =
        [
            PhpTypeDeclaration(),
            PhpFunction(),
        ],
        ["c"] =
        [
            CFunctionPrototype(),
            CTypedefDeclaration(),
            CStructDeclaration(),
            CDefineMacro(),
        ],
        ["cpp"] =
        [
            CppClassOrStruct(),
            CppNamespace(),
            CppFunctionSignature(),
            CppTypedef(),
        ],
        ["swift"] =
        [
            SwiftTypeDeclaration(),
            SwiftFunction(),
        ],
        ["python"] =
        [
            PythonClass(),
            PythonFunction(),
        ],
    };

    private static readonly Dictionary<string, string> LangByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ts"] = "ts", [".tsx"] = "ts", [".js"] = "ts", [".mjs"] = "ts", [".jsx"] = "ts", [".vue"] = "ts", [".svelte"] = "ts",
        [".kt"] = "kotlin", [".kts"] = "kotlin",
        [".cs"] = "csharp",
        [".php"] = "php",
        [".h"] = "c",
        [".hpp"] = "cpp", [".hh"] = "cpp",
        [".swift"] = "swift",
        [".py"] = "python",
    };

    /// <param name="onlyProject">Index only this registered project's name; when null, every project not
    /// named in <c>GRIMOIRA_SKIP_PROJECTS</c> (comma-separated) is indexed — the same env var
    /// index-code.mjs reads.</param>
    public string Execute(SqliteConnection connection, string? onlyProject, string backupDirectory)
    {
        SchemaRunResult schemaResult = SchemaRunner.Run(
            connection, [new Schema.GraphIndexSchema(), new Schema.GraphFileRelSchema(connection)], backupDirectory);
        if (!schemaResult.Success)
            return $"schema step failed: {schemaResult.Error}";
        Schema.GraphFileRelSchema.Backfill(connection);

        HashSet<string> skip = new(
            (Environment.GetEnvironmentVariable("GRIMOIRA_SKIP_PROJECTS") ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        List<(string name, string root)> projects = [];
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT name, root FROM projects";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
            {
                string name = r.GetString(0);
                if (onlyProject is not null ? name == onlyProject : !skip.Contains(name))
                    projects.Add((name, r.GetString(1)));
            }
        }

        // Zero registered projects (a fresh/empty instance, a typo'd --instance, or an --project name
        // that matches nothing) must not look the same as "ran fine, nothing new to add" (issue #2):
        // without this, both cases print the identical "total new edges: 0" and the caller has no way
        // to tell a wrong-instance silent no-op from real, already-indexed idempotency.
        if (projects.Count == 0)
        {
            return onlyProject is not null
                ? $"no registered project named '{onlyProject}' for this instance — nothing indexed."
                : "no registered projects for this instance — nothing indexed. Run `grimoira project --name <n> --root <dir>` first.";
        }

        HashSet<string> known = new(StringComparer.Ordinal);
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT symbol, file, line FROM edges";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) known.Add($"{r.GetString(0)} {r.GetString(1)} {r.GetInt32(2)}");
        }

        int grandTotal = 0, grandRemoved = 0;
        List<string> log = [];
        foreach ((string name, string root) in projects)
        {
            int files = 0, added = 0, removed = 0;
            using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                // This tool's own earlier rows for the project, keyed exactly as stored. Every key the
                // re-scan does not see again (gone file, moved declaration, non-normalized path) goes.
                Dictionary<string, long> existing = OwnRows(connection, transaction, name);
                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (string file in Walk(root))
                {
                    string ext = Path.GetExtension(file);
                    if (!LangByExt.TryGetValue(ext, out string? lang)) continue;
                    // index-code.mjs:175 stores the full path (its `rel` is misnamed — never made
                    // relative to the project root), not a root-relative one; matched here for parity.
                    // `file_rel` (slice 31) carries the real project-relative path alongside it.
                    string rel = NormalizePath(file);
                    string? fileRel = Schema.GraphFileRelSchema.ToRelative(root, file);
                    files++;
                    foreach ((string symbol, int line) in DeclarationsIn(file, lang))
                    {
                        string key = $"{symbol} {rel} {line}";
                        seen.Add(key);
                        if (existing.ContainsKey(key)) continue;
                        if (!known.Add(key)) continue;
                        using SqliteCommand insert = connection.CreateCommand();
                        insert.Transaction = transaction;
                        insert.CommandText =
                            "INSERT INTO edges(symbol, contract, project, file, line, usage, hardcoded, file_rel) VALUES($sym, 'decl', $proj, $file, $line, $usage, 0, $filerel)";
                        insert.Parameters.AddWithValue("$sym", symbol);
                        insert.Parameters.AddWithValue("$proj", name);
                        insert.Parameters.AddWithValue("$file", rel);
                        insert.Parameters.AddWithValue("$line", line);
                        insert.Parameters.AddWithValue("$usage", $"{lang} declaration");
                        insert.Parameters.AddWithValue("$filerel", (object?)fileRel ?? DBNull.Value);
                        insert.ExecuteNonQuery();
                        added++;
                    }
                }
                foreach ((string key, long id) in existing)
                {
                    if (seen.Contains(key)) continue;
                    DeleteRow(connection, transaction, id);
                    known.Remove(key);
                    removed++;
                }
                transaction.Commit();
            }
            catch (Exception e)
            {
                transaction.Rollback();
                log.Add($"  {name}: FAILED {e.Message}");
                continue;
            }
            grandTotal += added;
            grandRemoved += removed;
            log.Add($"  {name,-16} {files,6} files -> {added} new declaration(s), {removed} stale removed");
        }
        log.Add($"total new edges: {grandTotal}, stale removed: {grandRemoved}");
        return string.Join("\n", log);
    }

    /// <summary>Forward slashes, and an upper-case drive letter on Windows-style paths, so one file has
    /// exactly one stored path whichever spelling the project root was registered with.</summary>
    public static string NormalizePath(string path)
    {
        string p = path.Replace('\\', '/');
        return p.Length > 1 && p[1] == ':' && char.IsAsciiLetterLower(p[0]) ? char.ToUpperInvariant(p[0]) + p[1..] : p;
    }

    /// <summary>
    /// Deletes this tool's own rows for <paramref name="project"/> whose file lies under
    /// <paramref name="root"/> — what a workspace move (<c>project --name n --root new</c>) leaves behind
    /// under the old root. Curated rows (any other contract/usage) are never touched. Returns the count.
    /// </summary>
    public static int DeleteOwnRowsUnder(SqliteConnection connection, SqliteTransaction? transaction, string project, string root)
    {
        string prefix = NormalizePath(root).TrimEnd('/') + "/";
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        List<long> doomed = [];
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.Transaction = transaction;
            c.CommandText = $"SELECT id, file FROM edges WHERE project=$p AND contract='decl' AND usage IN ({OwnUsagesSql})";
            c.Parameters.AddWithValue("$p", project);
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(1) && NormalizePath(r.GetString(1)).StartsWith(prefix, cmp)) doomed.Add(r.GetInt64(0));
        }
        foreach (long id in doomed) DeleteRow(connection, transaction, id);
        return doomed.Count;
    }

    // The usage strings this tool writes ("ts declaration", "csharp declaration", ...): its own signature.
    private static readonly string OwnUsagesSql =
        string.Join(", ", Rules.Keys.Select(lang => $"'{lang} declaration'"));

    private static Dictionary<string, long> OwnRows(SqliteConnection connection, SqliteTransaction transaction, string project)
    {
        Dictionary<string, long> rows = new(StringComparer.Ordinal);
        using SqliteCommand c = connection.CreateCommand();
        c.Transaction = transaction;
        c.CommandText = $"SELECT id, symbol, file, line FROM edges WHERE project=$p AND contract='decl' AND usage IN ({OwnUsagesSql})";
        c.Parameters.AddWithValue("$p", project);
        using SqliteDataReader r = c.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(1) || r.IsDBNull(2) || r.IsDBNull(3)) continue;
            rows.TryAdd($"{r.GetString(1)} {r.GetString(2)} {r.GetInt32(3)}", r.GetInt64(0));
        }
        return rows;
    }

    private static void DeleteRow(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        using SqliteCommand d = connection.CreateCommand();
        d.Transaction = transaction;
        d.CommandText = "DELETE FROM edges WHERE id=$id";
        d.Parameters.AddWithValue("$id", id);
        d.ExecuteNonQuery();
    }

    // Prune heavy/vendored dirs during the walk; tolerate unreadable dirs.
    private static IEnumerable<string> Walk(string dir)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { yield break; }

        foreach (string full in entries)
        {
            string name = Path.GetFileName(full);
            if (SkipDir.Contains(name)) continue;
            if (Directory.Exists(full))
            {
                foreach (string f in Walk(full)) yield return f;
            }
            else if (LangByExt.ContainsKey(Path.GetExtension(name)) && !VendoredLibraryPath().IsMatchOrFalse(full.Replace('\\', '/')))
            {
                yield return full;
            }
        }
    }

    private static IEnumerable<(string symbol, int line)> DeclarationsIn(string file, string lang)
    {
        string text;
        try
        {
            if (new FileInfo(file).Length > MaxBytes) yield break;
            text = File.ReadAllText(file);
        }
        catch { yield break; }

        List<int> starts = [0];
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        int LineAt(int idx)
        {
            int lo = 0, hi = starts.Count - 1;
            while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (starts[mid] <= idx) lo = mid; else hi = mid - 1; }
            return lo + 1;
        }

        Dictionary<string, int> found = new(StringComparer.Ordinal);
        List<string> order = [];
        if (Rules.TryGetValue(lang, out Regex[]? patterns))
        {
            foreach (Regex re in patterns)
            {
                foreach (Match m in re.MatchesOrEmpty(text))
                {
                    string name = m.Groups[1].Value;
                    if (name.Length < 3) continue;
                    if (found.ContainsKey(name)) continue;
                    found[name] = LineAt(m.Index);
                    order.Add(name);
                }
            }
        }

        // A single-file Vue/Svelte component is addressed by its filename, not by an inner declaration.
        if (file.EndsWith(".vue", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".svelte", StringComparison.OrdinalIgnoreCase))
        {
            string comp = Path.GetFileNameWithoutExtension(file);
            if (comp.Length >= 3 && found.TryAdd(comp, 1)) order.Add(comp);
        }

        foreach (string name in order) yield return (name, found[name]);
    }

    [GeneratedRegex(@"[\\/]lib[\\/](freetype|brotli|harfbuzz|fribidi|libass|zlib|openssl|ffmpeg|expat|png|jpeg)[\\/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout.Milliseconds)]
    private static partial Regex VendoredLibraryPath();
    [GeneratedRegex(@"^\s*export\s+(?:default\s+)?(?:abstract\s+)?class\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex TypeScriptExportedClass();
    [GeneratedRegex(@"^\s*export\s+(?:declare\s+)?(?:async\s+)?function\s+\*?\s*([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex TypeScriptExportedFunction();
    [GeneratedRegex(@"^\s*export\s+(?:declare\s+)?(?:interface|type|enum)\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex TypeScriptExportedTypeDeclaration();
    [GeneratedRegex(@"^\s*export\s+(?:declare\s+)?(?:const|let|var)\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex TypeScriptExportedVariable();
    [GeneratedRegex(@"^\s*export\s+\{\s*([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex TypeScriptExportList();
    [GeneratedRegex(@"^\s*(?:public\s+|internal\s+)?(?:data\s+|sealed\s+|abstract\s+|open\s+|value\s+)*(?:class|interface|object)\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex KotlinTypeDeclaration();
    [GeneratedRegex(@"^\s*(?:public\s+|internal\s+)?(?:suspend\s+)?fun\s+(?:<[^>]+>\s*)?([A-Za-z_]\w*)\s*\(", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex KotlinFunction();
    [GeneratedRegex(@"^\s*(?:public\s+|internal\s+)?(?:const\s+)?val\s+([A-Z][A-Za-z0-9_]*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex KotlinConstant();
    [GeneratedRegex(@"^\s*(?:public|internal)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+)*(?:class|interface|record|struct|enum)\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex CSharpPublicType();
    [GeneratedRegex(@"^\s*public\s+(?:static\s+|virtual\s+|override\s+|async\s+|sealed\s+)*[A-Za-z_][\w<>,.\[\]?]*\s+([A-Za-z_]\w*)\s*\(", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex CSharpPublicMethod();
    [GeneratedRegex(@"^\s*(?:abstract\s+|final\s+)?(?:class|interface|trait|enum)\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex PhpTypeDeclaration();
    [GeneratedRegex(@"^\s*(?:public\s+|protected\s+)?(?:static\s+)?function\s+([A-Za-z_]\w*)\s*\(", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex PhpFunction();
    [GeneratedRegex(@"^\s*(?:extern\s+)?(?:const\s+|unsigned\s+|signed\s+|static\s+)*(?:struct\s+|enum\s+|union\s+)?[A-Za-z_]\w*\s*\**\s*([A-Za-z_]\w*)\s*\([^;{]*\)\s*;", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex CFunctionPrototype();
    [GeneratedRegex(@"^\s*typedef\s+(?:struct|enum|union)?\s*[^;]*?\b([A-Za-z_]\w*)\s*;", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex CTypedefDeclaration();
    [GeneratedRegex(@"^\s*(?:struct|enum|union)\s+([A-Za-z_]\w*)\s*\{", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex CStructDeclaration();
    [GeneratedRegex(@"^\s*#\s*define\s+([A-Za-z_]\w{3,})", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex CDefineMacro();
    [GeneratedRegex(@"^\s*(?:class|struct)\s+(?:[A-Z_]+\s+)?([A-Za-z_]\w*)\s*(?::|\{)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex CppClassOrStruct();
    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex CppNamespace();
    [GeneratedRegex(@"^\s*(?:template\s*<[^>]*>\s*)?(?:[A-Za-z_][\w:<>,\s*&]*\s+)?([A-Za-z_]\w*)\s*\([^;{]*\)\s*(?:const\s*)?(?:override\s*)?[;{]", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex CppFunctionSignature();
    [GeneratedRegex(@"^\s*typedef\s+[^;]*?\b([A-Za-z_]\w*)\s*;", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex CppTypedef();
    [GeneratedRegex(@"^\s*(?:public\s+|open\s+|internal\s+)?(?:final\s+)?(?:class|struct|enum|protocol|actor)\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex SwiftTypeDeclaration();
    [GeneratedRegex(@"^\s*(?:public\s+|open\s+|internal\s+)?func\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex SwiftFunction();
    [GeneratedRegex(@"^\s*class\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex PythonClass();
    [GeneratedRegex(@"^\s*(?:async\s+)?def\s+([A-Za-z_]\w*)", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex PythonFunction();
}
