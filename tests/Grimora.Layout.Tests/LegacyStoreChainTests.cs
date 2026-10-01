using System.Security.Cryptography;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Layout.Tests;

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
        Directory.CreateDirectory(Path.Combine(_home, ".grimora.partial"));
        File.WriteAllText(Path.Combine(_home, ".grimora.partial", "half.txt"), "half");

        Assert.False(LegacyStore.MoveIfNeeded(_home));

        Assert.False(Directory.Exists(Path.Combine(_home, ".grimoira")));
        Assert.Equal("half", File.ReadAllText(Path.Combine(_home, ".grimora.partial", "half.txt")));
    }

    [Fact]
    public void ANewFolderMeansNothingIsCopiedFromEitherOldFolder()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".grimoira"));
        MakeDb(Path.Combine(_home, ".grimora", "proj", "grimora.db"), 1);

        Assert.False(LegacyStore.MoveIfNeeded(_home));
        Assert.False(Directory.Exists(Path.Combine(_home, ".grimoira", "proj")));
    }
}
