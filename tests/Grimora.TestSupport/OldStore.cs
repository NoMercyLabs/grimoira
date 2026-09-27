namespace Grimora.TestSupport;

/// <summary>The frozen "before" binaries (the pinned oracle commits) still read and write the old store
/// <c>~/.aitm/&lt;instance&gt;/aitm.db</c>, while the code under test uses <c>~/.grimora</c>. This copies an instance
/// across in either direction (file names renamed) so an oracle and the new code see the same data, and
/// removes the old-name copy afterwards. It is the one place a test names the old store.</summary>
public static class OldStore
{
    public static string OldInstanceDir(string instance) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm", instance);

    public static void ToOld(string instance) =>
        Copy(GrimoraCliRunner.InstanceDir(instance), OldInstanceDir(instance), "grimora", "aitm");

    public static void FromOld(string instance) =>
        Copy(OldInstanceDir(instance), GrimoraCliRunner.InstanceDir(instance), "aitm", "grimora");

    /// <summary>Rewrites the old product, store folder and file name in an oracle's output to the new ones.</summary>
    public static string AsNew(string output) => output.Replace("aitm", "grimora", StringComparison.Ordinal);

    public static void Delete(string instance)
    {
        string dir = OldInstanceDir(instance);
        if (instance.StartsWith("test-", StringComparison.Ordinal) && Directory.Exists(dir))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    private static void Copy(string from, string to, string oldWord, string newWord)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        HashSet<string> kept =
            [.. Directory.GetFiles(from).Select(f => Path.GetFileName(f).Replace(oldWord, newWord, StringComparison.Ordinal))];
        foreach (string stale in Directory.GetFiles(to).Where(f => !kept.Contains(Path.GetFileName(f))))
        {
            try { File.Delete(stale); }
            catch (IOException) { /* held by another process; leave it */ }
        }
        foreach (string file in Directory.GetFiles(from))
        {
            string name = Path.GetFileName(file).Replace(oldWord, newWord, StringComparison.Ordinal);
            try { File.Copy(file, Path.Combine(to, name), true); }
            catch (IOException) { /* a lock file another process holds; the store files are what count */ }
        }
    }
}
