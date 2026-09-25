namespace Aitm.Server.Data;

/// <summary>
/// The pure, deterministic decisions behind <c>init.mjs</c>'s project-discovery step: which language
/// marker a directory carries, and whether one detected project root sits inside another. init.mjs
/// becomes <c>aitm init --full</c> (RESTRUCTURE.md slice 23); the orchestration around these decisions
/// (spawning the CLI to register, index and ingest — init.mjs steps 3-8) does not move in this slice.
/// </summary>
public sealed record ProjectMarker(string Language, string Globs);

public static class InitFull
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
}
