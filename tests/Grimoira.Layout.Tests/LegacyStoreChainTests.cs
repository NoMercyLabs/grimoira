using System.Security.Cryptography;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Layout.Tests;

// The store folder chain: C:/Users/patri/.grimoira is the store; when it is missing the first start copies C:/Users/patri/.grimora
// (the newer old store) or else C:/Users/patri/.aitm. The old folders are the backup: never written, never deleted.
public sealed class LegacyStoreChainTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "grimoira-chain-" + Guid.NewGuid().ToString("N"));

    public LegacyStoreChainTests() => Directory.CreateDirectory(_home);

    public void Dispose() => Directory.Delete(_home, true);

    private static void MakeDb(string path, int rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using SqliteConnection c = new($"Data Source={path};Pooling=False");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "create table t(x integer);" + string.Concat(Enumerable.Range(0, rows).Select(i => $"insert into t values({i});"));
        cmd.ExecuteNonQuery();
    }

    private static long Count(string path)
    {
        using SqliteConnection c = new($"Data Source={path};Pooling=False;Mode=ReadOnly");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "select count(*) from t";
        return (long)cmd.ExecuteScalar()!;
    }

    private static Dictionary<string, string> HashAll(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private string NewDb => Path.Combine(_home, ".grimoira", "proj", "grimoira.db");

    [Fact]
    public void TheGrimoraFolderIsCopiedAndItsDatabaseRenamed()
    {
        MakeDb(Path.Combine(_home, ".grimora", "proj", "grimora.db"), 4);
        Dictionary<string, string> before = HashAll(Path.Combine(_home, ".grimora"));

        Assert.True(LegacyStore.MoveIfNeeded(_home));

        Assert.Equal(4, Count(NewDb));
        Assert.False(File.Exists(Path.Combine(_home, ".grimoira", "proj", "grimora.db")));
        Assert.Equal(before, HashAll(Path.Combine(_home, ".grimora")));
    }

    [Fact]
    public void TheGrimoraFolderIsPreferredOverTheAitmFolder()
    {
        MakeDb(Path.Combine(_home, ".aitm", "proj", "aitm.db"), 2);
        MakeDb(Path.Combine(_home, ".grimora", "proj", "grimora.db"), 7);

        Assert.True(LegacyStore.MoveIfNeeded(_home));

        Assert.Equal(7, Count(NewDb));
        Assert.Contains(".grimora", File.ReadAllText(Path.Combine(_home, ".grimoira", ".migrated-from-grimora")));
    }

    [Fact]
    public void TheAitmFolderStillMovesWhenNoGrimoraFolderExists()
    {
        MakeDb(Path.Combine(_home, ".aitm", "proj", "aitm.db"), 3);

        Assert.True(LegacyStore.MoveIfNeeded(_home));

        Assert.Equal(3, Count(NewDb));
        Assert.True(Directory.Exists(Path.Combine(_home, ".aitm")));
    }

    [Fact]
    public void AStaleGrimoraPartialIsNeitherASourceNorTouched()
    {
        MakeDb(Path.Combine(_home, ".aitm", "proj", "aitm.db"), 6);
        Directory.CreateDirectory(Path.Combine(_home, ".grimora.partial"));
        File.WriteAllText(Path.Combine(_home, ".grimora.partial", "half.txt"), "half");

        Assert.True(LegacyStore.MoveIfNeeded(_home));

        Assert.Equal(6, Count(NewDb));
        Assert.Contains(".aitm", File.ReadAllText(Path.Combine(_home, ".grimoira", ".migrated-from-aitm")));
        Assert.False(File.Exists(Path.Combine(_home, ".grimoira", "half.txt")));
        Assert.Equal("half", File.ReadAllText(Path.Combine(_home, ".grimora.partial", "half.txt")));
    }

    [Fact]
    public void ANewFolderHoldingARealDatabaseMeansNothingIsCopiedFromEitherOldFolder()
    {
        MakeDb(NewDb, 1);
        MakeDb(Path.Combine(_home, ".grimora", "proj", "grimora.db"), 9);
        MakeDb(Path.Combine(_home, ".grimora", "other", "grimora.db"), 9);

        Assert.False(LegacyStore.MoveIfNeeded(_home));
        Assert.Equal(1, Count(NewDb));
        Assert.False(Directory.Exists(Path.Combine(_home, ".grimoira", "other")));
    }

    [Fact]
    public void ANewFolderMadeEarlyByALogAndAnEmptyInstanceFolderStillMigrates()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".grimoira", "emptyproj"));
        File.WriteAllText(Path.Combine(_home, ".grimoira", "hook.log"), "early");
        MakeDb(Path.Combine(_home, ".grimora", "proj", "grimora.db"), 5);

        Assert.True(LegacyStore.MoveIfNeeded(_home));

        Assert.Equal(5, Count(NewDb));
        Assert.Equal("early", File.ReadAllText(Path.Combine(_home, ".grimoira", "hook.log")));
        Assert.True(Directory.Exists(Path.Combine(_home, ".grimoira", "emptyproj")));
        Assert.True(File.Exists(Path.Combine(_home, ".grimoira", ".migrated-from-grimora")));
        Assert.False(LegacyStore.MoveIfNeeded(_home)); // done: the marker stops a second copy
    }

    [Fact]
    public void ANameClashBetweenAnEarlyFileAndAnOldFileKeepsBothAndTheEarlyFileStaysInPlace()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".grimoira", "proj"));
        File.WriteAllText(Path.Combine(_home, ".grimoira", "proj", "notes.txt"), "new");
        MakeDb(Path.Combine(_home, ".grimora", "proj", "grimora.db"), 2);
        File.WriteAllText(Path.Combine(_home, ".grimora", "proj", "notes.txt"), "old");

        Assert.True(LegacyStore.MoveIfNeeded(_home));

        Assert.Equal(2, Count(NewDb));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_home, ".grimoira", "proj", "notes.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_home, ".grimoira", "proj", "notes.txt.from-grimora")));
    }
}
