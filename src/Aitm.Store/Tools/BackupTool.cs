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
        string dest = to ?? Path.Combine(dir, $"aitm-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");

        // VACUUM INTO refuses to write over an existing file, but the old wal-checkpoint-then-File.Copy
        // this replaces always overwrote --to (aitm.cs:1983-1991, File.Copy(..., overwrite: true)).
        // Build into a temp file next to the destination, then swap it in, to keep that behaviour.
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
            File.Move(temp, dest, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return $"backed up -> {dest}";
    }
}
