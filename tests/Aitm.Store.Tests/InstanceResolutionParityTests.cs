using Aitm.Store.Data;
using Xunit;

namespace Aitm.Store.Tests;

// Pins aitm.cs's ResolveInstance() (aitm.cs:325-335) so the new code resolves the same instance name
// for the same inputs. AitmCsResolveInstance/AitmCsSlug below are a verbatim copy of that logic — the
// oracle this test checks the new code against, not a reimplementation of what the new code "should" do.
public class InstanceResolutionParityTests
{
    [Theory]
    [InlineData("NoMercy", null, "/home/owner/work")]
    [InlineData(null, "/home/owner/projects/aitm", "/tmp")]
    [InlineData(null, null, "/home/owner/projects/My Repo!!")]
    [InlineData(null, null, "/")]
    [InlineData("Weird Name_2", null, "/tmp")]
    [InlineData(null, "C:\\Projects\\NoMercy\\", "/tmp")]
    public void ResolvesTheSameInstanceAsAitmCs(string? aitmInstanceEnv, string? claudeProjectDirEnv, string currentDirectory)
    {
        string expected = AitmCsResolveInstance(aitmInstanceEnv, claudeProjectDirEnv, currentDirectory);
        string actual = StoreConnection.ResolveInstance(aitmInstanceEnv, claudeProjectDirEnv, currentDirectory);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("NoMercy")]
    [InlineData("My Repo!!")]
    [InlineData("Weird_Name-2")]
    [InlineData("")]
    public void SlugsTheSameAsAitmCs(string text)
    {
        Assert.Equal(AitmCsSlug(text), StoreConnection.Slug(text));
    }

    // --- verbatim copy of aitm.cs:325-335 ---
    private static string AitmCsResolveInstance(string? env, string? proj, string cwd)
    {
        if (!string.IsNullOrWhiteSpace(env)) return AitmCsSlug(env);
        string dir = string.IsNullOrWhiteSpace(proj) ? cwd : proj;
        string name = Path.GetFileName(dir.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? "default" : AitmCsSlug(name);
    }

    private static string AitmCsSlug(string text) =>
        new([.. text.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')]);
}
