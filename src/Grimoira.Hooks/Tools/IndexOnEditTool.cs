using System.Text.Json;
using Grimoira.Docs.Tools;
using Grimoira.Hooks.Data;
using Grimoira.Memory.Tools;
using Microsoft.Data.Sqlite;
using static Grimoira.Store.Data.JsonShape;

namespace Grimoira.Hooks.Tools;

/// <summary>
/// PostToolUse handler (Write|Edit|MultiEdit|NotebookEdit), ported verbatim (routing rules, the
/// cross-instance guard, fail-open) from index-on-edit.mjs (RESTRUCTURE.md slice 21, "Hooks, part 2"):
/// keeps the memory and docs channels from drifting by reindexing the touched channel right after a
/// successful edit, instead of waiting for a manual CLI run.
///
/// Routing (decided purely from the edited path, same as the .mjs):
///   - a `*.md` under `~/.claude/projects/&lt;encoded&gt;/memory/` (not MEMORY.md itself, and only when
///     the encoded segment ends with this instance's slug) -&gt; reindex that memory dir.
///   - anything under `&lt;project&gt;/.claude/docs/design/`                                  -&gt; reindex that design dir.
///   - a `*.md` the `docs` table already holds, or whose folder holds an indexed doc        -&gt; reindex that one file.
///   - anything else                                                                          -&gt; silent no-op.
///
/// Unlike the .mjs (which shells out to the prebuilt <c>grimoira.exe</c>), Grimoira.Hooks sits at reference level
/// 3 (RESTRUCTURE.md section 1; ReferenceDirectionTests), so it calls <see cref="IndexMemoryTool"/> and
/// <see cref="IndexDocsTool"/> in process. Never blocks or crashes the edit: any error, and the no-op
/// path, produce no output; a failure never echoes exception detail (indexed sources can be private).
/// </summary>
public static class IndexOnEditTool
{
    public static string Execute(string stdin) => Execute(stdin, projectDir: null);

    /// <param name="projectDir">The project the caller resolved (<see cref="HookPaths.ProjectDir"/>), or null.</param>
    public static string Execute(string stdin, string? projectDir)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;

            string? filePath = GetToolInputPath(payload);
            if (filePath is null) return "";

            string? cwd = GetString(payload, "cwd");
            string instance = HookPaths.ResolveInstance(cwd, projectDir);
            if (!File.Exists(HookPaths.DbPath(instance))) return "";

            string file = Path.GetFullPath(filePath);
            string proj = HookPaths.ProjectDir(cwd, projectDir);

            // (1) Memory channel: a *.md under ~/.claude/projects/<encoded>/memory/ — skip MEMORY.md
            // itself (the always-loaded index, which index-memory itself skips).
            string projectsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            string memoryDir = Path.GetDirectoryName(file) ?? "";
            if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(file).Equals("MEMORY.md", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(memoryDir).Equals("memory", StringComparison.OrdinalIgnoreCase)
                && IsInside(memoryDir, projectsRoot))
            {
                // Guard: the encoded `projects/<encoded>` segment must belong to this instance, so a
                // memory edit in another project never indexes into this one's brain.
                string encoded = Path.GetFileName(Path.GetDirectoryName(memoryDir) ?? "").ToLowerInvariant();
                if (encoded.EndsWith(instance, StringComparison.Ordinal))
                {
                    Reindex(instance, connection => new IndexMemoryTool().Execute(connection, memoryDir));
                }
                return "";
            }

            // (2) Docs channel: anything under <project>/.claude/docs/design/.
            string designDir = Path.Combine(proj, ".claude", "docs", "design");
            if (Directory.Exists(designDir) && IsInside(file, designDir))
            {
                Reindex(instance, connection => new IndexDocsTool().Execute(connection, designDir, "doc"));
                return "";
            }

            // (3) Known-folder channel: a *.md the store already holds, or one whose folder already holds an
            // indexed doc. Without this, every doc outside the design dir drifted after its first index
            // (seen: 163 of 1,260 plan files missing, edits to the main plan never reached the store).
            if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                Reindex(instance, connection =>
                {
                    if (!StoreHoldsFileOrItsFolder(connection, file)) return;
                    ShedDocRowsForFile(connection, file);
                    new IndexDocsTool().Execute(connection, file, "doc");
                });
                return "";
            }

            // (4) Everything else — fast silent no-op.
            return "";
        }
        catch
        {
            // fail open — a reindex error must never break the user's edit
            return "";
        }
    }

    private static void Reindex(string instance, Action<SqliteConnection> run)
    {
        try
        {
            using SqliteConnection connection = HookStore.Open(HookPaths.DbPath(instance));
            run(connection);
        }
        catch
        {
            // Never leak exception detail: indexed sources can contain private material.
        }
    }

    // One cheap query: does `docs` hold this exact path, or any doc directly in the same folder?
    // `docs.path` carries mixed drive-letter case and both slash kinds, so both sides are
    // normalised to lower-case forward slashes before the compare.
    private static bool StoreHoldsFileOrItsFolder(SqliteConnection connection, string file)
    {
        string key = DocKey(file);
        string folder = key[..(key.LastIndexOf('/') + 1)];
        using SqliteCommand probe = connection.CreateCommand();
        probe.CommandText = """
            SELECT 1 FROM docs
            WHERE lower(replace(path,'\','/')) = $file
               OR (substr(lower(replace(path,'\','/')), 1, length($folder)) = $folder
                   AND instr(substr(lower(replace(path,'\','/')), length($folder) + 1), '/') = 0)
            LIMIT 1
            """;
        probe.Parameters.AddWithValue("$file", key);
        probe.Parameters.AddWithValue("$folder", folder);
        return probe.ExecuteScalar() is not null;
    }

    // IndexDocsTool upserts per section key (`<path>#<idx>`), so a rewrite with fewer sections would leave
    // the old tail behind; drop the file's rows first so the store holds only the new content.
    private static void ShedDocRowsForFile(SqliteConnection connection, string file)
    {
        string key = DocKey(file);
        using (SqliteCommand fts = connection.CreateCommand())
        {
            fts.CommandText = "DELETE FROM docs_fts WHERE k IN (SELECT k FROM docs WHERE lower(replace(path,'\\','/')) = $file)";
            fts.Parameters.AddWithValue("$file", key);
            fts.ExecuteNonQuery();
        }
        using (SqliteCommand rows = connection.CreateCommand())
        {
            rows.CommandText = "DELETE FROM docs WHERE lower(replace(path,'\\','/')) = $file";
            rows.Parameters.AddWithValue("$file", key);
            rows.ExecuteNonQuery();
        }
    }

    // Same shape IndexDocsTool writes into docs.path, lower-cased (its section key is this plus `#<idx>`).
    private static string DocKey(string file) => Path.GetFullPath(file).Replace('\\', '/').ToLowerInvariant();

    // Case-insensitive (Windows) path-containment: is `child` inside `parent`?
    private static bool IsInside(string child, string parent)
    {
        string c = Norm(child);
        string p = Norm(parent);
        return c == p || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Norm(string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/').Replace('/', Path.DirectorySeparatorChar).ToLowerInvariant();

    private static string? GetToolInputPath(JsonElement payload)
    {
        if (!TryGetObjectProperty(payload, "tool_input", out JsonElement input) || input.ValueKind != JsonValueKind.Object) return null;
        return GetString(input, "file_path") ?? GetString(input, "notebook_path") ?? GetString(input, "path");
    }
}
