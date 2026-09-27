using System.Runtime.CompilerServices;

namespace Grimora.Layout.Tests;

// A test running under `dotnet test` has an unpredictable working directory (the test host's own
// folder). [CallerFilePath] pins the repo root to this source file's own location instead, so the
// layout tests find grimora.cs, mcp.cs and the src/ tree no matter how they are invoked.
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot([CallerFilePath] string here = "")
    {
        // this file lives at <root>/tests/Grimora.Layout.Tests/RepoPaths.cs
        string dir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    public static readonly string[] SourceProjects =
    [
        "Grimora.Store", "Grimora.Facts", "Grimora.Memory", "Grimora.Docs", "Grimora.Graph",
        "Grimora.Brain", "Grimora.Hooks", "Grimora.Server", "Grimora.Cli",
    ];

    public static IEnumerable<object[]> SourceProjectsData() => SourceProjects.Select(p => new object[] { p });
}
