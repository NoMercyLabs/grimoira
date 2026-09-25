using Aitm.Store.Data;
using Aitm.Store.Tests.Support;
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
