extern alias cli;

using Grimoira.Store.Data;
using Xunit;

namespace Grimoira.Layout.Tests;

[CollectionDefinition("EnvironmentMutation", DisableParallelization = true)]
public sealed class EnvironmentMutationCollection;

[Collection("EnvironmentMutation")]
public sealed class LegacyCompatTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "grimoira-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyCompatTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", null);
        Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", null);
        Directory.Delete(_home, true);
    }

    [Fact]
    public void OldEnvVarIsHonouredWhenTheNewOneIsUnset()
    {
        Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", null);
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", "/old/dir");
        LegacyEnvironment.Promote();
        Assert.Equal("/old/dir", Grimoira.Server.Data.ServerAddress.ResolveDataDir());

        Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", null);
        cli::Grimoira.Cli.Tools.LegacyEnvironment.Promote();
        Assert.Equal("/old/dir", cli::Grimoira.Cli.Tools.ServerAddress.ResolveDataDir());
    }

    [Fact]
    public void NewEnvVarWinsWhenBothAreSet()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", "/old/dir");
        Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", "/new/dir");
        LegacyEnvironment.Promote();
        cli::Grimoira.Cli.Tools.LegacyEnvironment.Promote();
        Assert.Equal("/new/dir", Grimoira.Server.Data.ServerAddress.ResolveDataDir());
        Assert.Equal("/new/dir", cli::Grimoira.Cli.Tools.ServerAddress.ResolveDataDir());
    }

    [Fact]
    public void AnEmptyNewVarCountsAsUnset()
    {
        Environment.SetEnvironmentVariable("AITM_DATA_DIR", "/old/dir");
        Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", "");
        LegacyEnvironment.Promote();
        Assert.Equal("/old/dir", Grimoira.Server.Data.ServerAddress.ResolveDataDir());
    }
}
