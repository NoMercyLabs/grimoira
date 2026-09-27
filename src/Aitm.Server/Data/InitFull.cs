using Aitm.Brain.Schema;
using Aitm.Docs.Tools;
using Aitm.Facts.Tools;
using Aitm.Facts.Schema;
using Aitm.Docs.Schema;
using Aitm.Graph.Schema;
using Aitm.Graph.Tools;
using Aitm.Memory.Schema;
using Aitm.Memory.Tools;
using Aitm.Store.Data;
using Aitm.Store.Schema;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Aitm.Server.Data;

/// <summary>
/// The pure, deterministic decisions behind <c>init.mjs</c>'s project-discovery step: which language
/// marker a directory carries, and whether one detected project root sits inside another. init.mjs
/// becomes <c>aitm init --full</c> (RESTRUCTURE.md slice 23); the orchestration around these decisions
/// (spawning the CLI to register, index and ingest — init.mjs steps 3-8) does not move in this slice.
/// </summary>
public sealed record ProjectMarker(string Language, string Globs);

/// <summary>The inputs <c>init --full</c> needs, injectable so a test never touches the real
/// <c>~/.aitm</c>, a live store, or a real repo (RESTRUCTURE.md slice 23).</summary>
public sealed record InitFullOptions(string Root, string Instance, string DbPath, string HomeDir, bool SkipChat);

/// <summary>The full transcript of an <c>init --full</c> run, in the same order init.mjs's own
/// <c>step()</c> log lines appear, plus whether every step that can fail did.</summary>
public sealed record InitFullResult(bool Success, string Log);

public static partial class InitFull
{
    // init.mjs MARKERS, in the same priority order.
    public static readonly IReadOnlyList<(string File, ProjectMarker Marker)> Markers =
    [
        ("package.json", new ProjectMarker("ts", "*.ts,*.vue")),
        ("composer.json", new ProjectMarker("php", "*.php")),
        ("build.gradle.kts", new ProjectMarker("kotlin", "*.kt")),
        ("build.gradle", new ProjectMarker("kotlin", "*.kt")),
        ("settings.gradle.kts", new ProjectMarker("kotlin", "*.kt")),
        ("go.mod", new ProjectMarker("go", "*.go")),
        ("Cargo.toml", new ProjectMarker("rust", "*.rs")),
        ("pyproject.toml", new ProjectMarker("python", "*.py")),
        ("CMakeLists.txt", new ProjectMarker("c", "*.h")),
        ("configure", new ProjectMarker("c", "*.h")),
        ("meson.build", new ProjectMarker("c", "*.h")),
    ];

    // init.mjs SKIP_DIR.
    public static readonly IReadOnlySet<string> SkipDirectories = new HashSet<string>(StringComparer.Ordinal)
    {
        "node_modules", ".git", "dist", "build", "bin", "obj", ".next", ".nuxt", ".gradle", ".idea",
        "vendor", "coverage", ".venv", "venv", "__pycache__", "Pods", "DerivedData", "target", "out",
        ".turbo", ".cache", "worktrees", ".claude",
    };

    /// <summary>
    /// The language marker for a directory: a named manifest file first, a bare .csproj/.sln pair for
    /// C# (which carries no single fixed manifest name), or null when nothing matches.
    /// </summary>
    public static ProjectMarker? Detect(string dir)
    {
        foreach ((string file, ProjectMarker marker) in Markers)
        {
            if (File.Exists(Path.Combine(dir, file))) return marker;
        }
        try
        {
            if (Directory.EnumerateFileSystemEntries(dir)
                .Any(f => f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".sln", StringComparison.Ordinal)))
            {
                return new ProjectMarker("csharp", "*.cs");
            }
        }
        catch
        {
            // unreadable
        }
        return null;
    }

    /// <summary>
    /// A gradle submodule or a workspace package sitting inside an already-detected project is part of
    /// that project, not a peer of it.
    /// </summary>
    public static bool IsInside(string child, string parent)
    {
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return false;
        string normalizedParent = parent.TrimEnd('\\', '/').ToLowerInvariant() + Path.DirectorySeparatorChar;
        return child.ToLowerInvariant().StartsWith(normalizedParent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Registration upserts on the name, so a bare basename is dangerous: two unrelated projects that
    /// share a common leaf name ("android", "app", "shared", "site") would silently overwrite each
    /// other's root. Null means no unique name could be derived.
    /// </summary>
    public static string? DeriveUniqueName(string dir, IReadOnlySet<string> takenNames)
    {
        string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (!takenNames.Contains(name.ToLowerInvariant())) return name;

        string? parentDir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
        if (parentDir is not null)
        {
            string qualified = $"{Path.GetFileName(parentDir)}-{name}";
            if (!takenNames.Contains(qualified.ToLowerInvariant())) return qualified;
        }
        return null;
    }

    // init.mjs's SKIP_DIR also prunes the discovery walk itself (init.mjs:94-104), on top of feeding
    // Detect(); Markers is walked in the same priority order Detect() checks them in.

    /// <summary>
    /// Cold start, as <c>aitm init --full</c> (RESTRUCTURE.md section 2.3, slice 23): register the
    /// projects under <paramref name="options"/>'s root, index their code, docs, memory rules and chat
    /// history. Every verb init.mjs used to spawn the CLI for (steps 1, 3-8) already lives in-process as
    /// a C# tool (slices 13-19), so this calls them directly; nothing here spawns a child process or a
    /// runner. Steps, messages, skip rules and exit codes are kept verbatim from init.mjs; every step
    /// upserts, so a second run is idempotent, same as the Node original.
    /// </summary>
    public static InitFullResult RunFull(InitFullOptions options)
    {
        List<string> log = [];
        void Step(int n, string message) => log.Add($"[{n}] {message}");

        string? dbDir = Path.GetDirectoryName(options.DbPath);
        string backupDirectory = Path.Combine(
            string.IsNullOrEmpty(dbDir) ? Path.GetTempPath() : dbDir, "init-full-backups");

        SqliteConnection connection;
        try
        {
            if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);
            connection = StoreConnection.Open(options.DbPath);
        }
        catch (Exception e)
        {
            log.Add("[1] CLI");
            log.Add($"  FAILED: could not open the store: {e.Message}");
            return new InitFullResult(false, string.Join('\n', log));
        }

        using (connection)
        {
            // ---------------------------------------------------------------- 1. the CLI
            // init.mjs builds/locates the CLI exe here (init.mjs:34-58); this code IS that CLI, already
            // running, so the equivalent step is opening/creating the instance (init.mjs's `init` call).
            Step(1, "CLI");
            // Old `aitm init` runs Init() then InitBrain() (aitm.cs:48-52) before init.mjs ever registers
            // a project, so a fresh `init --full` store needs BrainSchema (node/triple/slot/ref, ...) too
            // — without it the brain_* tools have nothing to write to on a store this command created.
            SchemaRunResult schema = SchemaRunner.Run(
                connection,
                [new StoreSchema(), new FactsSchema(), new MemorySchema(), new DocsSchema(), new GraphSchema(), new BrainSchema()],
                backupDirectory);
            if (!schema.Success)
            {
                log.Add($"  FAILED: could not build the CLI. schema step failed: {schema.Error}");
                return new InitFullResult(false, string.Join('\n', log));
            }
            log.Add($"  {new InitTool().Execute(options.Instance, options.DbPath)}");

            // ------------------------------------------------------- 2. discover projects
            Step(2, "projects");
            RunProjectDiscovery(connection, options.Root, log);

            // --------------------------------------------------------------- 3. the code
            Step(3, "code surface");
            string codeLog = new IndexCodeTool().Execute(connection, null, backupDirectory);
            foreach (string line in codeLog.Split('\n')) log.Add($"  {line}");

            // --------------------------------------------------------------- 4. packages
            Step(4, "package identities");
            log.Add($"  {new IndexPackagesTool().Execute(connection, options.Root)}");

            // ------------------------------------------------------------------- 5. docs
            Step(5, "docs");
            foreach (string rel in new[] { ".claude", "docs" })
            {
                string dir = Path.Combine(options.Root, rel);
                if (!Directory.Exists(dir)) continue;
                log.Add($"  {rel}: {new IndexDocsTool().Execute(connection, dir, "doc")}");
            }

            // ----------------------------------------------------------------- 6. memory
            Step(6, "rules");
            string memDir = Path.Combine(options.HomeDir, ".claude", "projects", PathSlug(options.Root), "memory");
            log.Add(Directory.Exists(memDir) ? $"  {new IndexMemoryTool().Execute(connection, memDir)}" : "  no memory directory for this repo");

            // ---------------------------------------------------- 7. past conversations
            Step(7, "past conversations");
            if (options.SkipChat)
            {
                log.Add("  skipped");
            }
            else
            {
                string projectsDir = Path.Combine(options.HomeDir, ".claude", "projects");
                string prefix = PathSlug(options.Root);
                List<string> dirs = [];
                if (Directory.Exists(projectsDir))
                {
                    foreach (string d in Directory.EnumerateDirectories(projectsDir))
                    {
                        string name = Path.GetFileName(d);
                        if (name == prefix || name.StartsWith($"{prefix}-", StringComparison.Ordinal)) dirs.Add(d);
                    }
                }

                int sessions = dirs.Sum(d => Directory.Exists(d) ? Directory.EnumerateFiles(d, "*.jsonl").Count() : 0);
                log.Add($"  {dirs.Count} transcript director(ies), {sessions} session(s)");
                foreach (string d in dirs)
                {
                    string result = new IndexChatTool().Execute(connection, d);
                    log.Add($"    {Path.GetFileName(d)}: {(string.IsNullOrWhiteSpace(result) ? "nothing new" : result)}");
                }
            }

            // ---------------------------------------------------------------- 8. summary
            Step(8, "ready");
            log.Add(new StatsTool().Execute(connection, options.Instance, options.DbPath));
        }

        return new InitFullResult(true, string.Join('\n', log));
    }

    // init.mjs step 2 (init.mjs:60-155): discover project roots, drop nested and vanished ones, register
    // whatever is left under a name that cannot collide with one already taken.
    private static void RunProjectDiscovery(SqliteConnection connection, string root, List<string> log)
    {
        Dictionary<string, ProjectMarker> found = new(StringComparer.OrdinalIgnoreCase);
        Scan(root, root, 0, found);
        if (found.Count == 0)
        {
            ProjectMarker? rootMarker = Detect(root);
            if (rootMarker is not null) found[Path.GetFullPath(root)] = rootMarker;
        }
        foreach (string dir in found.Keys.ToList())
        {
            if (found.Keys.Any(other => IsInside(dir, other))) found.Remove(dir);
        }

        // A project whose root has vanished keeps answering with paths that are gone.
        foreach ((string name, string projRoot) in ListProjects(connection))
        {
            if (Directory.Exists(projRoot) || File.Exists(projRoot)) continue;
            new ForgetProjectTool().Execute(connection, root, name);
            log.Add($"  - {name} (root gone: {projRoot})");
        }

        List<(string Name, string Root)> known = ListProjects(connection);
        HashSet<string> knownRoots = new(known.Select(p => Path.GetFullPath(p.Root).ToLowerInvariant()), StringComparer.Ordinal);
        HashSet<string> takenNames = new(known.Select(p => p.Name.ToLowerInvariant()), StringComparer.Ordinal);

        int registered = 0;
        foreach ((string dir, ProjectMarker meta) in found)
        {
            string normalized = dir.ToLowerInvariant();
            if (knownRoots.Contains(normalized)) continue;
            if (knownRoots.Any(k => IsInside(dir, k))) continue;

            string? name = DeriveUniqueName(dir, takenNames);
            if (name is null) { log.Add($"  ! skipped {dir} (cannot derive a unique name)"); continue; }
            takenNames.Add(name.ToLowerInvariant());

            new ProjectTool().Execute(connection, name, dir, meta.Language, meta.Globs);
            registered++;
            log.Add($"  + {name,-28} {meta.Language}");
        }
        log.Add($"  {registered} newly registered, {found.Count - registered} already known");
    }

    // init.mjs:94-104, depth-capped: a marker buried deeper than this is a fixture or an example.
    private static void Scan(string root, string dir, int depth, Dictionary<string, ProjectMarker> found)
    {
        if (depth > 3) return;
        ProjectMarker? hit = Detect(dir);
        if (hit is not null && !string.Equals(Path.GetFullPath(dir), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            found[Path.GetFullPath(dir)] = hit;

        IEnumerable<string> entries;
        try { entries = Directory.EnumerateDirectories(dir); }
        catch { return; }

        foreach (string sub in entries)
        {
            string name = Path.GetFileName(sub);
            if (SkipDirectories.Contains(name) || name.StartsWith('.')) continue;
            Scan(root, sub, depth + 1, found);
        }
    }

    private static List<(string Name, string Root)> ListProjects(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name, root FROM projects ORDER BY name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string, string)> rows = [];
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    // init.mjs's root.replace(/[:\\/]+/g, '-'), used both for the memory directory and the chat
    // transcript-directory prefix match.
    private static string PathSlug(string path) =>
        PathSeparatorRun().ReplaceOrKeep(path, "-");

    [GeneratedRegex(@"[:\\/]+", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PathSeparatorRun();
}
