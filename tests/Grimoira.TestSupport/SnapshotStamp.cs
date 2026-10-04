namespace Grimoira.TestSupport;

/// <summary>
/// The <c>.built-ok</c> stamp of a cached oracle build (the pinned mcp.dll snapshot and the slice-24 CLI
/// oracle, both under the user temp directory). It lists every file the build produced, so a cache a
/// clean-up has half emptied (2026-10-04: the dependency dlls gone, the main dll and the stamp left
/// behind; 77 parity tests red because the oracle answered nothing) is rebuilt instead of trusted.
/// </summary>
public static class SnapshotStamp
{
    private const string FileName = ".built-ok";

    /// <summary>Writes the stamp listing every file now in <paramref name="dir"/>.</summary>
    public static void Write(string dir) =>
        File.WriteAllLines(Path.Combine(dir, FileName), Directory.GetFiles(dir).Select(Path.GetFileName)!);

    /// <summary>True when <paramref name="dir"/> still holds every file its stamp lists. A stamp from
    /// before the file list (a timestamp) never counts.</summary>
    public static bool IsComplete(string dir)
    {
        string stamp = Path.Combine(dir, FileName);
        if (!File.Exists(stamp)) return false;
        string[] listed = [.. File.ReadAllLines(stamp).Where(l => l.Length > 0)];
        return listed.Length > 0 && listed.All(f => File.Exists(Path.Combine(dir, f)));
    }
}
