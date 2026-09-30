using System.Text.Json;
using Grimora.Docs.Tools;
using Grimora.Hooks.Data;
using Grimora.Memory.Tools;
using Microsoft.Data.Sqlite;
using static Grimora.Store.Data.JsonShape;

namespace Grimora.Hooks.Tools;

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
///   - anything else                                                                          -&gt; silent no-op.
///
/// Unlike the .mjs (which shells out to the prebuilt <c>grimora.exe</c>), Grimora.Hooks sits at reference level
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

            // (3) Everything else — fast silent no-op.
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
