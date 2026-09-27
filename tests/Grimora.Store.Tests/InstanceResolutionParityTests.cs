using Grimora.Store.Data;
using Xunit;

namespace Grimora.Store.Tests;

// Pins grimora.cs's ResolveInstance() (grimora.cs:325-335) so the new code resolves the same instance name
// for the same inputs. GrimoraCsResolveInstance/GrimoraCsSlug below are a verbatim copy of that logic — the
// oracle this test checks the new code against, not a reimplementation of what the new code "should" do.
public class InstanceResolutionParityTests
{
    [Theory]
    [InlineData("NoMercy", null, "/home/owner/work")]
    [InlineData(null, "/home/owner/projects/grimora", "/tmp")]
    [InlineData(null, null, "/home/owner/projects/My Repo!!")]
    [InlineData(null, null, "/")]
    [InlineData("Weird Name_2", null, "/tmp")]
    [InlineData(null, "C:\\Projects\\NoMercy\\", "/tmp")]
    public void ResolvesTheSameInstanceAsGrimoraCs(string? grimoraInstanceEnv, string? claudeProjectDirEnv, string currentDirectory)
    {
        string expected = GrimoraCsResolveInstance(grimoraInstanceEnv, claudeProjectDirEnv, currentDirectory);
        string actual = StoreConnection.ResolveInstance(grimoraInstanceEnv, claudeProjectDirEnv, currentDirectory);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("NoMercy")]
    [InlineData("My Repo!!")]
    [InlineData("Weird_Name-2")]
    [InlineData("")]
    public void SlugsTheSameAsGrimoraCs(string text)
    {
        Assert.Equal(GrimoraCsSlug(text), StoreConnection.Slug(text));
    }

    // --- verbatim copy of grimora.cs:325-335 ---
    private static string GrimoraCsResolveInstance(string? env, string? proj, string cwd)
    {
        if (!string.IsNullOrWhiteSpace(env)) return GrimoraCsSlug(env);
        string dir = string.IsNullOrWhiteSpace(proj) ? cwd : proj;
        string name = Path.GetFileName(dir.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? "default" : GrimoraCsSlug(name);
    }

    private static string GrimoraCsSlug(string text) =>
        new([.. text.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')]);
}
