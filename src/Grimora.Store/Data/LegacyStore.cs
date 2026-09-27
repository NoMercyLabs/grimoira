namespace Grimora.Store.Data;

/// <summary>Compat shim (slice 32c): the store folder was <c>~/.aitm</c>; it is <c>~/.grimora</c> now. Runs once,
/// when the new folder is missing and the old one exists: back up, copy across (files named for the old product
/// get the new name), and leave the old folder as <c>.aitm.moved-&lt;date&gt;</c>. When both exist the new one
/// wins and nothing moves.</summary>
public static class LegacyStore
{
    public static bool MoveIfNeeded(string home, DateTime? now = null)
    {
        string oldDir = Path.Combine(home, ".aitm");
        string newDir = Path.Combine(home, ".grimora");
        if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return false;

        string stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss");
        CopyTree(oldDir, Path.Combine(home, $".aitm.backup-{stamp}"), false);
        string partial = newDir + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, true);
        CopyTree(oldDir, partial, true);
        Directory.Move(partial, newDir);
        Directory.Move(oldDir, Path.Combine(home, $".aitm.moved-{stamp}"));
        return true;
    }

    private static void CopyTree(string from, string to, bool rename)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Renamed(Path.GetFileName(file), rename)), true);
        foreach (string dir in Directory.GetDirectories(from))
            CopyTree(dir, Path.Combine(to, Renamed(Path.GetFileName(dir), rename)), rename);
    }

    private static string Renamed(string name, bool rename) =>
        rename ? name.Replace("aitm", "grimora", StringComparison.OrdinalIgnoreCase) : name;
}
