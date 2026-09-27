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

    [Fact]
    public void AnEmptyNewVarCountsAsUnset()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", "/old/dir");
        Environment.SetEnvironmentVariable("GRIMORA_DATA_DIR", "");
        LegacyEnvironment.Promote();
        Assert.Equal("/old/dir", Grimora.Server.Data.ServerAddress.ResolveDataDir());
    }
}
