using Grimora.Store.Data;
using Xunit;

namespace Grimora.Layout.Tests;

[CollectionDefinition("EnvironmentMutation", DisableParallelization = true)]
public sealed class EnvironmentMutationCollection;

[Collection("EnvironmentMutation")]
public sealed class LegacyCompatTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "grimora-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyCompatTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", null);
        Environment.SetEnvironmentVariable("GRIMORA_DATA_DIR", null);
        Directory.Delete(_home, true);
    }

    private string MakeOld()
    {
        string inst = Path.Combine(_home, ".aitm", "proj");
        Directory.CreateDirectory(inst);
        File.WriteAllText(Path.Combine(inst, "aitm.db"), "data-1");
        return inst;
    }

    [Fact]
    public void OldStoreMovesOnceWithBackupAndLeavesTheOldFolder()
    {
        MakeOld();
        Assert.True(LegacyStore.MoveIfNeeded(_home));

        Assert.Equal("data-1", File.ReadAllText(Path.Combine(_home, ".grimora", "proj", "grimora.db")));
        Assert.False(Directory.Exists(Path.Combine(_home, ".aitm")));
        Assert.Single(Directory.GetDirectories(_home, ".aitm.moved-*"));
        string[] backups = Directory.GetDirectories(_home, ".aitm.backup-*");
        Assert.Single(backups);
        Assert.Equal("data-1", File.ReadAllText(Path.Combine(backups[0], "proj", "aitm.db")));

        Assert.False(LegacyStore.MoveIfNeeded(_home));
        Assert.Single(Directory.GetDirectories(_home, ".aitm.moved-*"));
    }

    [Fact]
    public void WhenBothExistTheNewOneWinsAndNothingMoves()
    {
        MakeOld();
        Directory.CreateDirectory(Path.Combine(_home, ".grimora"));
        File.WriteAllText(Path.Combine(_home, ".grimora", "keep.txt"), "new");

        Assert.False(LegacyStore.MoveIfNeeded(_home));
        Assert.True(Directory.Exists(Path.Combine(_home, ".aitm")));
        Assert.Empty(Directory.GetDirectories(_home, ".aitm.moved-*"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_home, ".grimora", "keep.txt")));
    }

    [Fact]
    public void OldEnvVarIsHonouredWhenTheNewOneIsUnset()
    {
        Environment.SetEnvironmentVariable("GRIMORA_DATA_DIR", null);
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", "/old/dir");
        LegacyEnvironment.Promote();
        Assert.Equal("/old/dir", Grimora.Server.Data.ServerAddress.ResolveDataDir());

        Environment.SetEnvironmentVariable("GRIMORA_DATA_DIR", null);
        Grimora.Cli.Tools.LegacyEnvironment.Promote();
        Assert.Equal("/old/dir", Grimora.Cli.Tools.ServerAddress.ResolveDataDir());
    }

    [Fact]
    public void NewEnvVarWinsWhenBothAreSet()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", "/old/dir");
        Environment.SetEnvironmentVariable("GRIMORA_DATA_DIR", "/new/dir");
        LegacyEnvironment.Promote();
        Grimora.Cli.Tools.LegacyEnvironment.Promote();
        Assert.Equal("/new/dir", Grimora.Server.Data.ServerAddress.ResolveDataDir());
        Assert.Equal("/new/dir", Grimora.Cli.Tools.ServerAddress.ResolveDataDir());
    }
}
