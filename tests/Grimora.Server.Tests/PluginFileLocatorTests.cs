using Grimora.Server.Data;
using Xunit;

namespace Grimora.Server.Tests;

// RESTRUCTURE.md slice 32a: an installed plugin runs the server from ${CLAUDE_PLUGIN_DATA}/current/bin-server,
// outside the plugin root. Files that ship in the plugin root (idp-impersonate.mjs, seeds/spine.json)
// are found through the plugin root the SessionStart step passes as GRIMORA_PLUGIN_ROOT; GRIMORA_HOME still wins for
// the engine, and a checkout build still finds them next to or above its own folder.
public class PluginFileLocatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("grimora-plugin-files-").FullName;

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
    public void GrimoraHomeStillWinsOverThePluginRoot()
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

    // A server started by the logon task or a thin client has no GRIMORA_PLUGIN_ROOT. SessionStart records the
    // plugin root in <data>/plugin-root.txt, two folders above the server's own (builds/<id>/bin-server, or
    // current/bin-server through the junction).
    [Theory]
    [InlineData("builds", "380d3d1ab3e7")]
    [InlineData("current", null)]
    public void AServerWithoutThePluginRootVariableFindsTheEngineThroughTheDataFolderFile(string first, string? second)
    {
        string pluginRoot = Folder("plugin");
        File.WriteAllText(Path.Combine(pluginRoot, "idp-impersonate.mjs"), "");
        string data = Folder("data");
        File.WriteAllText(Path.Combine(data, "plugin-root.txt"), pluginRoot);
        string serverDir = second is null ? Folder("data", first, "bin-server") : Folder("data", first, second, "bin-server");

        Assert.Equal(Path.Combine(pluginRoot, "idp-impersonate.mjs"),
            PluginFileLocator.FindEngine("idp-impersonate.mjs", home: null, pluginRoot: null, serverDir));
    }

    [Fact]
    public void AStaleDataFolderFileFallsThroughToTheWalkUp()
    {
        string data = Folder("data");
        File.WriteAllText(Path.Combine(data, "plugin-root.txt"), Path.Combine(_dir, "removed-plugin-version"));
        File.WriteAllText(Path.Combine(data, "idp-impersonate.mjs"), "");
        string serverDir = Folder("data", "builds", "380d3d1ab3e7", "bin-server");

        Assert.Equal(Path.Combine(data, "idp-impersonate.mjs"),
            PluginFileLocator.FindEngine("idp-impersonate.mjs", home: null, pluginRoot: null, serverDir));
    }

    [Fact]
    public void APluginRootVariableThatNoLongerExistsFallsThroughToTheDataFolderFile()
    {
        string pluginRoot = Folder("plugin");
        string data = Folder("data");
        File.WriteAllText(Path.Combine(data, "plugin-root.txt"), pluginRoot);
        string serverDir = Folder("data", "builds", "380d3d1ab3e7", "bin-server");

        Assert.Equal(Path.Combine(pluginRoot, "seeds", "spine.json"),
            PluginFileLocator.SeedPath(Path.Combine(_dir, "removed-plugin-version"), serverDir));
    }

    [Fact]
    public void TheSeedFileIsFoundThroughTheDataFolderFileWithoutTheVariable()
    {
        string pluginRoot = Folder("plugin");
        string data = Folder("data");
        File.WriteAllText(Path.Combine(data, "plugin-root.txt"), pluginRoot);

        Assert.Equal(Path.Combine(pluginRoot, "seeds", "spine.json"),
            PluginFileLocator.SeedPath(pluginRoot: null, Folder("data", "current", "bin-server")));
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
