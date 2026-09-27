using Grimora.Store.Data;
using System.Text.RegularExpressions;
using Grimora.Store.Schema;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Graph.Tools;

/// <summary>
/// Bulk code indexer: fills <c>edges</c> with the PUBLIC DECLARATION surface of every registered
/// project, so the brain can answer "what is X / where does X live" without a tree scan. Declarations
/// only, not usages — usages explode the table and the exported surface is what questions are about.
/// Copied verbatim (rules, skip list, walk order, idempotency) from <c>index-code.mjs</c>
/// (RESTRUCTURE.md slice 14).
///
/// Idempotent: an existing (symbol,file,line) row is left alone, so this can run on every session end.
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
        "index-code [--project <name>]  bulk-index every registered project's public declaration surface into edges";

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
    /// named in <c>GRIMORA_SKIP_PROJECTS</c> (comma-separated) is indexed — the same env var
    /// index-code.mjs reads.</param>
    public string Execute(SqliteConnection connection, string? onlyProject, string backupDirectory)
    {
        SchemaRunResult schemaResult = SchemaRunner.Run(
            connection, [new Schema.GraphIndexSchema(), new Schema.GraphFileRelSchema(connection)], backupDirectory);
        if (!schemaResult.Success)
            return $"schema step failed: {schemaResult.Error}";
        Schema.GraphFileRelSchema.Backfill(connection);

        HashSet<string> skip = new(
            (Environment.GetEnvironmentVariable("GRIMORA_SKIP_PROJECTS") ?? "")
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

        HashSet<string> known = new(StringComparer.Ordinal);
        using (SqliteCommand c = connection.CreateCommand())
        {
            c.CommandText = "SELECT symbol, file, line FROM edges";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) known.Add($"{r.GetString(0)} {r.GetString(1)} {r.GetInt32(2)}");
        }

        int grandTotal = 0;
        List<string> log = [];
        foreach ((string name, string root) in projects)
        {
            int files = 0, added = 0;
            using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                foreach (string file in Walk(root))
                {
                    string ext = Path.GetExtension(file);
                    if (!LangByExt.TryGetValue(ext, out string? lang)) continue;
                    // index-code.mjs:175 stores the full path (its `rel` is misnamed — never made
                    // relative to the project root), not a root-relative one; matched here for parity.
                    // `file_rel` (slice 31) carries the real project-relative path alongside it.
                    string rel = file.Replace('\\', '/');
                    string? fileRel = Schema.GraphFileRelSchema.ToRelative(root, file);
                    files++;
                    foreach ((string symbol, int line) in DeclarationsIn(file, lang))
                    {
                        string key = $"{symbol} {rel} {line}";
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
                transaction.Commit();
            }
            catch (Exception e)
            {
                transaction.Rollback();
                log.Add($"  {name}: FAILED {e.Message}");
                continue;
            }
            grandTotal += added;
            log.Add($"  {name,-16} {files,6} files -> {added} new declaration(s)");
        }
        log.Add($"total new edges: {grandTotal}");
        return string.Join("\n", log);
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
