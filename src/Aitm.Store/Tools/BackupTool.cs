using Microsoft.Data.Sqlite;

namespace Aitm.Store.Tools;

/// <summary>
/// Backs up the live store. RESTRUCTURE.md slice 4: "<c>backup</c> switches from a file copy to
/// <c>VACUUM INTO</c> (safe while others read)". The old aitm.cs (aitm.cs:1983-1991) checkpointed WAL
/// then copied the file, which can race a concurrent writer; <c>VACUUM INTO</c> takes a read
/// transaction on the source and writes a fresh, compacted copy without blocking readers.
/// </summary>
public sealed class BackupTool : ITool
{
    public string Name => "backup";
    public string CliVerb => "backup";
    public string? McpName => null;
    public string Help => "backup [--to <path>]                 snapshot the instance store (VACUUM INTO)";

    public string Execute(SqliteConnection connection, string root, string? to)
    {
        string dir = Path.Combine(root, "backups");
        Directory.CreateDirectory(dir);

        // An explicit --to is a single named destination the caller chose, so it keeps the old
        // File.Copy(overwrite: true) behaviour (aitm.cs:1983-1991). A default (automatic) name is
        // minted fresh every call — timestamp to the millisecond plus a random suffix — so the many
        // automatic callers (delete/bulk-change tools) never collide with each other, and never with
        // an earlier automatic backup: it fails loud instead of silently replacing it.
        bool automatic = to is null;
        string dest = to ?? Path.Combine(dir, AutomaticName());

        // VACUUM INTO refuses to write over an existing file. Build into a temp file next to the
        // destination, then swap it in, so an explicit --to can still overwrite atomically.
        string destDir = Path.GetDirectoryName(Path.GetFullPath(dest)) is { Length: > 0 } d ? d : ".";
        Directory.CreateDirectory(destDir);
        string temp = Path.Combine(destDir, $".{Path.GetFileName(dest)}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $path";
                command.Parameters.AddWithValue("$path", temp);
                command.ExecuteNonQuery();
            }
            File.Move(temp, dest, overwrite: !automatic);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return $"backed up -> {dest}";
    }

    /// <summary>Millisecond timestamp plus an 8-hex-char random suffix, so two automatic backups in the
    /// same millisecond still get distinct names — the old <c>aitm-yyyyMMdd-HHmmss.db</c> (one-second
    /// resolution, no suffix) let two callers in the same second collide and the second overwrite the
    /// first (RESTRUCTURE.md; hit by <c>ForgetProjectToolTests</c>). Keeps the existing "aitm-" prefix
    /// and lexical, chronological sort order, so anything listing the backups folder still finds every
    /// file and still sees the newest one last.</summary>
    private static string AutomaticName()
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        string suffix = Guid.NewGuid().ToString("N")[..8];
        return $"aitm-{timestamp}-{suffix}.db";
    }
}
