using Aitm.Server.Data;
using Xunit;

namespace Aitm.Server.Tests;

// Ported from init.mjs's project-discovery step (no .mjs test existed for it; init.mjs itself is the
// oracle for these pure decisions — see the detect()/isInside()/name-uniqueness logic there).
public class InitFullTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aitm-init-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void DetectsATypeScriptProjectByPackageJson()
    {
        File.WriteAllText(Path.Combine(_dir, "package.json"), "{}");

        ProjectMarker? marker = InitFull.Detect(_dir);

        Assert.NotNull(marker);
        Assert.Equal("ts", marker!.Language);
    }

    [Fact]
    public void DetectsACSharpProjectByCsprojWhenNoNamedManifestMatches()
    {
        File.WriteAllText(Path.Combine(_dir, "Widget.csproj"), "<Project />");

        ProjectMarker? marker = InitFull.Detect(_dir);

        Assert.NotNull(marker);
        Assert.Equal("csharp", marker!.Language);
    }

    [Fact]
    public void DetectsNothingWhenNoMarkerIsPresent()
    {
        Assert.Null(InitFull.Detect(_dir));
    }

    [Fact]
    public void NamedManifestsWinOverTheBareCsprojFallback()
    {
        File.WriteAllText(Path.Combine(_dir, "go.mod"), "module x");
        File.WriteAllText(Path.Combine(_dir, "Widget.csproj"), "<Project />");

        ProjectMarker? marker = InitFull.Detect(_dir);

        Assert.Equal("go", marker!.Language);
    }

    [Fact]
    public void ChildProjectIsInsideItsParent()
    {
        string parent = Path.Combine(_dir, "monorepo");
        string child = Path.Combine(parent, "packages", "app");

        Assert.True(InitFull.IsInside(child, parent));
        Assert.False(InitFull.IsInside(parent, parent));
        Assert.False(InitFull.IsInside(parent, child));
    }

    [Fact]
    public void SiblingDirectoriesAreNeverInsideEachOther()
    {
        string a = Path.Combine(_dir, "app-one");
        string b = Path.Combine(_dir, "app-one-other");

        Assert.False(InitFull.IsInside(b, a));
    }

    [Fact]
    public void DerivesTheBareNameWhenItIsFree()
    {
        string dir = Path.Combine(_dir, "shared");
        Assert.Equal("shared", InitFull.DeriveUniqueName(dir, new HashSet<string>()));
    }

    [Fact]
    public void QualifiesWithTheParentDirectoryWhenTheBareNameIsTaken()
    {
        string dir = Path.Combine(_dir, "clients", "shared");
        string name = InitFull.DeriveUniqueName(dir, new HashSet<string> { "shared" })!;
        Assert.Equal("clients-shared", name);
    }

    [Fact]
    public void ReturnsNullWhenEvenTheQualifiedNameIsTaken()
    {
        string dir = Path.Combine(_dir, "clients", "shared");
        HashSet<string> taken = new() { "shared", "clients-shared" };
        Assert.Null(InitFull.DeriveUniqueName(dir, taken));
    }
}
