using System.Runtime.CompilerServices;

namespace Aitm.Layout.Tests;

// A test running under `dotnet test` has an unpredictable working directory (the test host's own
// folder). [CallerFilePath] pins the repo root to this source file's own location instead, so the
// layout tests find aitm.cs, mcp.cs and the src/ tree no matter how they are invoked.
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot([CallerFilePath] string here = "")
    {
        // this file lives at <root>/tests/Aitm.Layout.Tests/RepoPaths.cs
        string dir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    public static readonly string[] SourceProjects =
    [
        "Aitm.Store", "Aitm.Facts", "Aitm.Memory", "Aitm.Docs", "Aitm.Graph",
        "Aitm.Brain", "Aitm.Hooks", "Aitm.Server", "Aitm.Cli",
    ];

    public static IEnumerable<object[]> SourceProjectsData() => SourceProjects.Select(p => new object[] { p });
}
