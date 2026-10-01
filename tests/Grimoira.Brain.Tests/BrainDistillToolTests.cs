using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainDistillToolTests
{
    // Oracle: today's grimoira.cs BrainDistill (grimoira.cs:2077-2151), reached via CLI `brain distill`.

    [Fact]
    public void MatchesTodaysCliOutputAndDistillsMemoryFactsAndEdgesIntoTheGraph()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-distill-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-distill-new");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            SeedChannels(oldDb);
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain distill --instance {oldInstance}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            SeedChannels(newDb);

            string actual;
            using (SqliteConnection connection = StoreConnection.Open(newDb))
            {
                actual = new BrainDistillTool().Execute(connection, GrimoiraCliRunner.InstanceDir(newInstance)).Trim();
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
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void ReRunningDistillIsIdempotentAndLogsNoPhantomMutations()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-distill-idempotent");
        string root = GrimoiraCliRunner.InstanceDir(instance);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
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

            // BackupTool names an automatic snapshot to the millisecond plus an 8-hex-char random suffix
            // (fix 9d417e1), so two back-to-back runs never collide on "output file already exists" —
            // no sleep needed to dodge a same-second name.
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
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void MakesABackupBeforeDistillingAndTheBackupPredatesTheNewNodes()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-distill-backup");
        string root = GrimoiraCliRunner.InstanceDir(instance);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
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
            GrimoiraCliRunner.DeleteInstance(instance);
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
