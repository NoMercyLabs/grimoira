using System.Security.Cryptography;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Layout.Tests;

// The store move must never damage the old store: it copies each database with the SQLite backup API, leaves
// the old folder exactly as it is (it is the backup), and writes the new folder in one step.
public sealed class LegacyStoreMoveTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "grimora-move-" + Guid.NewGuid().ToString("N"));

    public LegacyStoreMoveTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        Directory.Delete(_home, true);
    }

    private string OldDb => Path.Combine(_home, ".aitm", "proj", "aitm.db");
    private string NewDb => Path.Combine(_home, ".grimora", "proj", "grimora.db");

    private static SqliteConnection Open(string path, string extra = "")
    {
        SqliteConnection c = new($"Data Source={path};Pooling=False{extra}");
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Count(string path)
    {
        using SqliteConnection c = Open(path, ";Mode=ReadOnly");
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "select count(*) from t";
        return (long)cmd.ExecuteScalar()!;
    }

    private void MakeOld(int rows = 3)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OldDb)!);
        using SqliteConnection c = Open(OldDb);
        Exec(c, "create table t(x integer)");
        for (int i = 0; i < rows; i++) Exec(c, $"insert into t values({i})");
        File.WriteAllText(Path.Combine(_home, ".aitm", "proj", "pending-learn.jsonl"), "line");
    }

    private static Dictionary<string, string> HashAll(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void RowsCommittedInTheWalOfAnOpenOldConnectionAreCopied()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OldDb)!);
        using SqliteConnection old = Open(OldDb);
        Exec(old, "pragma journal_mode=wal");
        Exec(old, "pragma wal_autocheckpoint=0");
        Exec(old, "create table t(x integer)");
        for (int i = 0; i < 5; i++) Exec(old, $"insert into t values({i})");
        Assert.True(File.Exists(OldDb + "-wal"));

        Assert.True(LegacyStore.MoveIfNeeded(_home));
        Assert.Equal(5, Count(NewDb));
    }

    [Fact]
    public async Task TwoConcurrentMoversMoveExactlyOnceAndBothSeeACompleteStore()
    {
        MakeOld(50);
        Task<bool>[] movers = [Task.Run(() => LegacyStore.MoveIfNeeded(_home)), Task.Run(() => LegacyStore.MoveIfNeeded(_home))];
        bool[] results = await Task.WhenAll(movers);

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(50, Count(NewDb));
        Assert.False(Directory.Exists(Path.Combine(_home, ".grimora.partial")));
    }

    [Fact]
    public void AStalePartialFromACrashedRunIsCleanedByTheLockHolder()
    {
        MakeOld();
        Directory.CreateDirectory(Path.Combine(_home, ".grimora.partial", "junk"));
        File.WriteAllText(Path.Combine(_home, ".grimora.partial", "junk", "x.txt"), "half");

        Assert.True(LegacyStore.MoveIfNeeded(_home));
        Assert.Equal(3, Count(NewDb));
        Assert.False(Directory.Exists(Path.Combine(_home, ".grimora", "junk")));
        Assert.False(Directory.Exists(Path.Combine(_home, ".grimora.partial")));
    }

    [Fact]
    public void TheOldFolderIsByteIdenticalAfterTheMove()
    {
        MakeOld();
        Dictionary<string, string> before = HashAll(Path.Combine(_home, ".aitm"));

        Assert.True(LegacyStore.MoveIfNeeded(_home));
        Assert.Equal(before, HashAll(Path.Combine(_home, ".aitm")));
        Assert.DoesNotContain(Directory.GetDirectories(_home), d => Path.GetFileName(d).StartsWith(".aitm.", StringComparison.Ordinal));
    }

    [Fact]
    public void AMarkerIsWrittenAndASecondStartDoesNothing()
    {
        MakeOld();
        Assert.True(LegacyStore.MoveIfNeeded(_home));
        string marker = Path.Combine(_home, ".grimora", ".migrated-from-aitm");
        Assert.Contains(Path.Combine(_home, ".aitm"), File.ReadAllText(marker));

        File.WriteAllText(Path.Combine(_home, ".grimora", "later.txt"), "new");
        Assert.False(LegacyStore.MoveIfNeeded(_home));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_home, ".grimora", "later.txt")));
    }

    [Fact]
    public void AFailureMidCopyLeavesNoNewFolderAndThrowsNothing()
    {
        MakeOld();
        string bad = Path.Combine(_home, ".aitm", "zbad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "aitm.db"), "this is not a database");
        StringWriter log = new();

        bool moved = LegacyStore.MoveIfNeeded(_home, log);

        Assert.False(moved);
        Assert.False(Directory.Exists(Path.Combine(_home, ".grimora")));
        Assert.False(Directory.Exists(Path.Combine(_home, ".grimora.partial")));
        Assert.NotEqual("", log.ToString());
    }

    [Fact]
    public void WhenBothFoldersExistTheNewOneWinsAndNothingMoves()
    {
        MakeOld();
        Directory.CreateDirectory(Path.Combine(_home, ".grimora"));
        Assert.False(LegacyStore.MoveIfNeeded(_home));
        Assert.False(Directory.Exists(Path.Combine(_home, ".grimora", "proj")));
    }
}
