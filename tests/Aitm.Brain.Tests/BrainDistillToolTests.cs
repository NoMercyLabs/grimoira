using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainDistillToolTests
{
    // Oracle: today's aitm.cs BrainDistill (aitm.cs:2077-2151), reached via CLI `brain distill`.

    [Fact]
    public void MatchesTodaysCliOutputAndDistillsMemoryFactsAndEdgesIntoTheGraph()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-distill-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-distill-new");
        try
        {
            AitmCliRunner.Run($"init --instance {oldInstance}");
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            SeedChannels(oldDb);
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain distill --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            AitmCliRunner.Run($"init --instance {newInstance}");
            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            SeedChannels(newDb);

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(newDb))
            {
                actual = new BrainDistillTool().Execute(connection, AitmCliRunner.InstanceDir(newInstance)).Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("distilled: 1 rule nodes, 1 fact nodes, 1 symbol nodes, 0 related links (0 unresolved -> distill_log). source channels untouched.", actual);

            using SqliteConnection check = StoreConnection.Open(newDb);

            using (SqliteCommand ruleNode = check.CreateCommand())
            {
                ruleNode.CommandText = "SELECT count(*) FROM node_now WHERE k='rule:mem1'";
                Assert.Equal(1L, (long)ruleNode.ExecuteScalar()!);
            }
            using (SqliteCommand factNode = check.CreateCommand())
            {
                factNode.CommandText = "SELECT count(*) FROM node_now WHERE k='fact:fact1'";
                Assert.Equal(1L, (long)factNode.ExecuteScalar()!);
            }
            using (SqliteCommand symbolNode = check.CreateCommand())
            {
                symbolNode.CommandText = "SELECT count(*) FROM node_now WHERE k='contract:TestDto.field1'";
                Assert.Equal(1L, (long)symbolNode.ExecuteScalar()!);
            }
            using (SqliteCommand consumes = check.CreateCommand())
            {
                consumes.CommandText = "SELECT count(*) FROM triple_now WHERE p='consumes' AND o='contract:TestDto.field1'";
                Assert.Equal(1L, (long)consumes.ExecuteScalar()!);
            }
            using (SqliteCommand memChannel = check.CreateCommand())
            {
                memChannel.CommandText = "SELECT count(*) FROM memory WHERE k='mem1'";
                Assert.Equal(1L, (long)memChannel.ExecuteScalar()!);
            }

            // source channels untouched
            using (SqliteCommand factChannel = check.CreateCommand())
            {
                factChannel.CommandText = "SELECT count(*) FROM facts WHERE k='fact1'";
                Assert.Equal(1L, (long)factChannel.ExecuteScalar()!);
            }
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ReRunningDistillIsIdempotentAndLogsNoPhantomMutations()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-distill-idempotent");
        string root = AitmCliRunner.InstanceDir(instance);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            SeedChannels(dbPath);

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new BrainDistillTool().Execute(connection, root);
            }

            long mutationsAfterFirstRun;
            using (SqliteConnection check = StoreConnection.Open(dbPath))
            using (SqliteCommand count = check.CreateCommand())
            {
                count.CommandText = "SELECT count(*) FROM mutations WHERE kind='node' AND k LIKE 'rule:%' OR kind='node' AND k LIKE 'fact:%' OR kind='node' AND k LIKE 'contract:%'";
                mutationsAfterFirstRun = (long)count.ExecuteScalar()!;
            }

            // BackupTool names its snapshot to the second (aitm-yyyyMMdd-HHmmss.db); back-to-back runs
            // inside the same second would collide on "output file already exists".
            Thread.Sleep(1100);
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new BrainDistillTool().Execute(connection, root);
            }

            using SqliteConnection recheck = StoreConnection.Open(dbPath);
            using SqliteCommand recount = recheck.CreateCommand();
            recount.CommandText = "SELECT count(*) FROM mutations WHERE kind='node' AND k LIKE 'rule:%' OR kind='node' AND k LIKE 'fact:%' OR kind='node' AND k LIKE 'contract:%'";
            Assert.Equal(mutationsAfterFirstRun, (long)recount.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void MakesABackupBeforeDistillingAndTheBackupPredatesTheNewNodes()
    {
        string instance = AitmCliRunner.NewTestInstance("brain-distill-backup");
        string root = AitmCliRunner.InstanceDir(instance);
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            SeedChannels(dbPath);

            string backupsDir = Path.Combine(root, "backups");
            Assert.False(Directory.Exists(backupsDir) && Directory.EnumerateFiles(backupsDir).Any());

            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                new BrainDistillTool().Execute(connection, root);
            }

            Assert.True(Directory.Exists(backupsDir));
            string backupPath = Directory.EnumerateFiles(backupsDir).Single();

            using SqliteConnection backup = new($"Data Source={backupPath};Mode=ReadOnly");
            backup.Open();
            using SqliteCommand c = backup.CreateCommand();
            c.CommandText = "SELECT count(*) FROM node_now WHERE k='rule:mem1'";
            Assert.Equal(0L, (long)c.ExecuteScalar()!);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static void SeedChannels(string dbPath)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);

        using (SqliteCommand mem = connection.CreateCommand())
        {
            mem.CommandText = "INSERT INTO memory(k,type,title,hook,body,links,hard) VALUES('mem1','feedback','Mem One','hook text','body text','',0)";
            mem.ExecuteNonQuery();
        }
        using (SqliteCommand fact = connection.CreateCommand())
        {
            fact.CommandText = "INSERT INTO facts(k,term,aliases,category,value,source,notes) VALUES('fact1','term1','','cat','val','src','')";
            fact.ExecuteNonQuery();
        }
        using (SqliteCommand edge = connection.CreateCommand())
        {
            edge.CommandText = "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES('field1','TestDto','web','src/x.ts',1,'a.field1',0)";
            edge.ExecuteNonQuery();
        }
    }
}
