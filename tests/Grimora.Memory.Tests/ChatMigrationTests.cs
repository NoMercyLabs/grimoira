using Grimora.Memory.Schema;
using Grimora.Memory.Tools;
using Grimora.Store.Data;
using Grimora.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Memory.Tests;

public sealed class ChatMigrationTests
{
    [Fact]
    public void ReportsAndImportsOnlyMissingOldRows()
    {
        string root = Path.Combine(Path.GetTempPath(), "grimora-chat-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string oldPath = Path.Combine(root, "old.db");
        string newPath = Path.Combine(root, "new.db");
        try
        {
            using (SqliteConnection old = StoreConnection.Open(oldPath))
            {
                SchemaRunner.Apply(old, [new MemorySchema()]);
                using SqliteCommand row = old.CreateCommand();
                row.CommandText = "INSERT INTO chat(k,session,ts,role,text) VALUES('s:one','s','2026-09-24','user','old message')";
                row.ExecuteNonQuery();
            }
            using SqliteConnection current = StoreConnection.Open(newPath);
            SchemaRunner.Apply(current, [new MemorySchema()]);
            ChatHistorySchema.Ensure(current);
            ChatMigrationTool tool = new();
            Assert.Contains("missing 1", tool.Compare(current, oldPath));
            Assert.Contains("imported 1", tool.ImportMissing(current, oldPath));
            Assert.Contains("missing 0", tool.Compare(current, oldPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
