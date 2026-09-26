using Aitm.Server.Data;
using Xunit;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md slice 32a: an installed plugin runs the server from ${CLAUDE_PLUGIN_DATA}/current/bin-server,
// outside the plugin root. Files that ship in the plugin root (idp-impersonate.mjs, seeds/spine.json)
// are found through the plugin root the SessionStart step passes as AITM_PLUGIN_ROOT; AITM_HOME still wins for
// the engine, and a checkout build still finds them next to or above its own folder.
public class PluginFileLocatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aitm-plugin-files-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Folder(params string[] parts)
    {
        string path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void TheEngineIsFoundInThePluginRootWhenTheServerRunsFromTheDataFolder()
    {
        string pluginRoot = Folder("plugin");
        File.WriteAllText(Path.Combine(pluginRoot, "idp-impersonate.mjs"), "");
        string serverDir = Folder("data", "current", "bin-server");

        Assert.Equal(Path.Combine(pluginRoot, "idp-impersonate.mjs"),
            PluginFileLocator.FindEngine("idp-impersonate.mjs", home: null, pluginRoot, serverDir));
    }

    [Fact]
    public void AitmHomeStillWinsOverThePluginRoot()
    {
        string home = Folder("home");
        File.WriteAllText(Path.Combine(home, "idp-impersonate.mjs"), "");
        string pluginRoot = Folder("plugin");
        File.WriteAllText(Path.Combine(pluginRoot, "idp-impersonate.mjs"), "");

        Assert.Equal(Path.Combine(home, "idp-impersonate.mjs"),
            PluginFileLocator.FindEngine("idp-impersonate.mjs", home, pluginRoot, Folder("data", "bin-server")));
    }

    [Fact]
    public void ACheckoutBuildStillFindsTheEngineAboveItsOwnFolder()
    {
        string checkout = Folder("checkout");
        File.WriteAllText(Path.Combine(checkout, "idp-impersonate.mjs"), "");
        string serverDir = Folder("checkout", "bin-server");

        Assert.Equal(Path.Combine(checkout, "idp-impersonate.mjs"),
            PluginFileLocator.FindEngine("idp-impersonate.mjs", home: null, pluginRoot: null, serverDir));
    }

    [Fact]
    public void TheSeedFileIsInThePluginRootWhenOneIsGiven()
    {
        string pluginRoot = Folder("plugin");
        Assert.Equal(Path.Combine(pluginRoot, "seeds", "spine.json"),
            PluginFileLocator.SeedPath(pluginRoot, Folder("data", "current", "bin-server")));
    }

    [Fact]
    public void WithoutAPluginRootTheSeedFileIsBesideTheCheckoutBuild()
    {
        string serverDir = Folder("checkout", "bin-server");
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "checkout", "seeds", "spine.json")),
            Path.GetFullPath(PluginFileLocator.SeedPath(pluginRoot: null, serverDir)));
    }
}
