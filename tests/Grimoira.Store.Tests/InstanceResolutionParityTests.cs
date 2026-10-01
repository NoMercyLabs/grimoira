using Grimoira.Store.Data;
using Xunit;

namespace Grimoira.Store.Tests;

// Pins grimoira.cs's ResolveInstance() (grimoira.cs:325-335) so the new code resolves the same instance name
// for the same inputs. GrimoiraCsResolveInstance/GrimoiraCsSlug below are a verbatim copy of that logic — the
// oracle this test checks the new code against, not a reimplementation of what the new code "should" do.
public class InstanceResolutionParityTests
{
    [Theory]
    [InlineData("NoMercy", null, "/home/dev/work")]
    [InlineData(null, "/home/dev/projects/grimoira", "/tmp")]
    [InlineData(null, null, "/home/dev/projects/My Repo!!")]
    [InlineData(null, null, "/")]
    [InlineData("Weird Name_2", null, "/tmp")]
    [InlineData(null, "C:\\Projects\\NoMercy\\", "/tmp")]
    public void ResolvesTheSameInstanceAsGrimoiraCs(string? grimoiraInstanceEnv, string? claudeProjectDirEnv, string currentDirectory)
    {
        string expected = GrimoiraCsResolveInstance(grimoiraInstanceEnv, claudeProjectDirEnv, currentDirectory);
        string actual = StoreConnection.ResolveInstance(grimoiraInstanceEnv, claudeProjectDirEnv, currentDirectory);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("NoMercy")]
    [InlineData("My Repo!!")]
    [InlineData("Weird_Name-2")]
    [InlineData("")]
    public void SlugsTheSameAsGrimoiraCs(string text)
    {
        Assert.Equal(GrimoiraCsSlug(text), StoreConnection.Slug(text));
    }

    // --- verbatim copy of grimoira.cs:325-335 ---
    private static string GrimoiraCsResolveInstance(string? env, string? proj, string cwd)
    {
        if (!string.IsNullOrWhiteSpace(env)) return GrimoiraCsSlug(env);
        string dir = string.IsNullOrWhiteSpace(proj) ? cwd : proj;
        string name = Path.GetFileName(dir.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? "default" : GrimoiraCsSlug(name);
    }

    private static string GrimoiraCsSlug(string text) =>
        new([.. text.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')]);
}
