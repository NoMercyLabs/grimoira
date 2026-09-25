using Aitm.Store.Data;
using Aitm.TestSupport;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

// RESTRUCTURE.md slice 4: "`backup` switches from a file copy to `VACUUM INTO` ... The test proves the
// old and new copies hold the same rows."
public class BackupToolTests
{
    [Fact]
    public void VacuumIntoCopyHoldsTheSameRowsAsTheOldFileCopyBackup()
    {
        string instance = AitmCliRunner.NewTestInstance("backup");
        string oldCopyPath = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-oldbackup-{Guid.NewGuid():N}.db");
        string newCopyPath = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-newbackup-{Guid.NewGuid():N}.db");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"add --instance {instance} --term backup-fixture-one --value one --category manual");
            AitmCliRunner.Run($"add --instance {instance} --term backup-fixture-two --value two --category manual");
            AitmCliRunner.Run($"todo --instance {instance} --title backup-fixture-todo");

            // Oracle: today's file-copy backup (aitm.cs:1983-1991).
            (string stdout, int exitCode) = AitmCliRunner.Run($"backup --instance {instance} --to \"{oldCopyPath}\"");
            Assert.Equal(0, exitCode);
            Assert.Contains(oldCopyPath, stdout);
            Assert.True(File.Exists(oldCopyPath));

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            Dictionary<string, long> originalCounts = RowCounts(dbPath);
            Dictionary<string, long> oldCopyCounts = RowCounts(oldCopyPath);

            // New: BackupTool's VACUUM INTO.
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                string message = new BackupTool().Execute(connection, AitmCliRunner.InstanceDir(instance), newCopyPath);
                Assert.Contains(newCopyPath, message);
            }
            Assert.True(File.Exists(newCopyPath));
            Dictionary<string, long> newCopyCounts = RowCounts(newCopyPath);

            Assert.Equal(originalCounts, oldCopyCounts);
            Assert.Equal(originalCounts, newCopyCounts);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string path in new[] { oldCopyPath, newCopyPath })
            {
                try { File.Delete(path); } catch (IOException) { }
            }
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    // Oracle: the old aitm.cs Backup() (aitm.cs:1983-1991) checkpointed the WAL then did
    // File.Copy(dbPath, dest, overwrite: true) — a second backup to the same --to path always
    // succeeded and replaced the earlier file. VACUUM INTO refuses to write over an existing file, so
    // BackupTool has to build into a temp file and swap it into place to keep that behaviour.
    [Fact]
    public void OverwritesAnExistingDestinationFileLikeTheOldFileCopyBackup()
    {
        string instance = AitmCliRunner.NewTestInstance("backup-overwrite");
        string dest = Path.Combine(Path.GetTempPath(), $"aitm-store-tests-overwrite-{Guid.NewGuid():N}.db");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            AitmCliRunner.Run($"add --instance {instance} --term backup-overwrite-one --value one --category manual");

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new BackupTool().Execute(connection, AitmCliRunner.InstanceDir(instance), dest);
            }
            Assert.True(File.Exists(dest));

            AitmCliRunner.Run($"add --instance {instance} --term backup-overwrite-two --value two --category manual");
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                string message = new BackupTool().Execute(connection, AitmCliRunner.InstanceDir(instance), dest);
                Assert.Contains(dest, message);
            }

            Dictionary<string, long> secondCounts = RowCounts(dest);
            Assert.True(secondCounts["facts"] >= 2);
            AssertIntegrityOk(dest);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dest); } catch (IOException) { }
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    // Bug: BackupTool's automatic (default-named) backup was named to the second
    // (aitm-yyyyMMdd-HHmmss.db), and always File.Move(overwrite: true) into place. Two automatic
    // backups of the same store made back-to-back land in the same second: the second one silently
    // replaced the first, and the rows the first backup captured were gone for good.
    [Fact]
    public void TwoAutomaticBackupsCalledBackToBackBothExistAndPassIntegrityCheck()
    {
        string instance = AitmCliRunner.NewTestInstance("backup-automatic-collision");
        string root = AitmCliRunner.InstanceDir(instance);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            AitmCliRunner.Run($"add --instance {instance} --term backup-auto-one --value one --category manual");

            string backupsDir = Path.Combine(root, "backups");
            string firstPath;
            string secondPath;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                string firstMessage = new BackupTool().Execute(connection, root, null);
                firstPath = ExtractBackupPath(firstMessage, backupsDir);
            }
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                string secondMessage = new BackupTool().Execute(connection, root, null);
                secondPath = ExtractBackupPath(secondMessage, backupsDir);
            }

            Assert.NotEqual(firstPath, secondPath);
            Assert.True(File.Exists(firstPath), $"first automatic backup missing: {firstPath}");
            Assert.True(File.Exists(secondPath), $"second automatic backup missing: {secondPath}");
            AssertIntegrityOk(firstPath);
            AssertIntegrityOk(secondPath);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string ExtractBackupPath(string message, string backupsDir)
    {
        const string marker = "backed up -> ";
        int index = message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"unexpected backup message: {message}");
        string path = message[(index + marker.Length)..].Trim();
        Assert.StartsWith(backupsDir, path);
        return path;
    }

    private static void AssertIntegrityOk(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", (string)check.ExecuteScalar()!);
    }

    private static Dictionary<string, long> RowCounts(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        List<string> tables = [];
        using (SqliteCommand list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '%_fts%'";
            using SqliteDataReader reader = list.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        Dictionary<string, long> counts = [];
        foreach (string table in tables)
        {
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = $"SELECT count(*) FROM {table}";
            counts[table] = (long)(count.ExecuteScalar() ?? 0L);
        }
        return counts;
    }
}
