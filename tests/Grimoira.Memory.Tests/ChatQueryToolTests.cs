using Grimoira.Memory.Schema;
using Grimoira.Memory.Tools;
using Grimoira.Store.Data;
using Grimoira.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Memory.Tests;

public sealed class ChatQueryToolTests
{
    [Fact]
    public void LegacyRowsWithRecognizableNoisePrefixesAreLabeledDuringMigration()
    {
        string path = Path.Combine(Path.GetTempPath(), "grimoira-legacy-kinds-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(path);
            SchemaRunner.Apply(connection, [new MemorySchema()]);
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO chat(k,session,ts,role,text) VALUES" +
                "('s:1','s','2026-09-28','user','Stop hook feedback: continue')," +
                "('s:2','s','2026-09-28','user','<command-name>/goal</command-name><command-args>finish</command-args>')," +
                "('s:3','s','2026-09-28','user','ordinary owner request')";
            insert.ExecuteNonQuery();
            ChatHistorySchema.Ensure(connection);
            using SqliteCommand kinds = connection.CreateCommand();
            kinds.CommandText = "SELECT group_concat(kind, ',') FROM (SELECT kind FROM chat ORDER BY k)";
            Assert.Equal("hook_feedback,slash_command,human", kinds.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            foreach (string backup in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".pre-chat-schema-*.db"))
                File.Delete(backup);
        }
    }

    [Fact]
    public void ListReportsTotalAndRemainingWhileFilteringKinds()
    {
        string path = Path.Combine(Path.GetTempPath(), "grimoira-chat-query-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(path);
            SchemaRunner.Apply(connection, [new MemorySchema()]);
            ChatHistorySchema.Ensure(connection);
            foreach ((string k, string ts, string kind) in new[]
            {
                ("s:1", "2026-09-28T00:00:00Z", "human"),
                ("s:2", "2026-09-28T00:01:00Z", "assistant"),
                ("s:3", "2026-09-28T00:02:00Z", "human"),
            })
            {
                using SqliteCommand insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO chat(k,session,ts,role,text,kind) VALUES($k,'s',$ts,'user','hello',$kind)";
                insert.Parameters.AddWithValue("$k", k);
                insert.Parameters.AddWithValue("$ts", ts);
                insert.Parameters.AddWithValue("$kind", kind);
                insert.ExecuteNonQuery();
            }
            string page = new ChatQueryTool().List(connection, "s", "human", "", "", 1, 1, false);
            Assert.Contains("total 2", page);
            Assert.Contains("remaining 1", page);
            Assert.Contains("s:1", page);
            Assert.DoesNotContain("s:2", page);
            string wholeDay = new ChatQueryTool().List(connection, "s", "human", "2026-09-28", "2026-09-28", 1, 10, false);
            Assert.Contains("total 2", wholeDay);
            Assert.Contains("s:3", wholeDay);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void RecallCountsAllHumanMatchesBeforeLimitingHits()
    {
        string path = Path.Combine(Path.GetTempPath(), "grimoira-recall-count-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(path);
            SchemaRunner.Apply(connection, [new MemorySchema()]);
            ChatHistorySchema.Ensure(connection);
            for (int i = 0; i < 7; i++)
            {
                string k = "s:" + i;
                using SqliteCommand insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO chat(k,session,ts,role,text,kind) VALUES($k,'s','2026-09-28','user','deploy KMP device',$kind); " +
                    "INSERT INTO chat_fts(k,text) VALUES($k,'deploy KMP device')";
                insert.Parameters.AddWithValue("$k", k);
                insert.Parameters.AddWithValue("$kind", i == 6 ? "hook_feedback" : "human");
                insert.ExecuteNonQuery();
            }
            string result = new RecallTool().ExecuteCli(connection, "deploy KMP");
            Assert.Contains("6 total", result);
            Assert.DoesNotContain("7 total", result);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void CountGroupsFtsMatchesByMonth()
    {
        string path = Path.Combine(Path.GetTempPath(), "grimoira-chat-month-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(path);
            SchemaRunner.Apply(connection, [new MemorySchema()]);
            ChatHistorySchema.Ensure(connection);
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO chat(k,session,ts,role,text,kind) VALUES('s:1','s','2026-09-28T00:00:00Z','user','deploy KMP device','human'); " +
                "INSERT INTO chat_fts(k,text) VALUES('s:1','deploy KMP device')";
            insert.ExecuteNonQuery();
            string result = new ChatQueryTool().Count(connection, "deploy KMP", "human", "month");
            Assert.Contains("total 1", result);
            Assert.Contains("2026-09: 1", result);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void CommandsCanSelectOneSlashCommandWithArguments()
    {
        string path = Path.Combine(Path.GetTempPath(), "grimoira-commands-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using SqliteConnection connection = StoreConnection.Open(path);
            SchemaRunner.Apply(connection, [new MemorySchema()]);
            ChatHistorySchema.Ensure(connection);
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO chat(k,session,ts,role,text,kind) VALUES" +
                "('s:1','s','2026-09-28','user','<command-name>/goal</command-name><command-args>finish</command-args>','slash_command')," +
                "('s:2','s','2026-09-28','user','/plugin restart','slash_command')";
            insert.ExecuteNonQuery();
            string result = new ChatQueryTool().Commands(connection, command: "/goal", full: true);
            Assert.Contains("total 1", result);
            Assert.Contains("finish", result);
            Assert.DoesNotContain("/plugin", result);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
