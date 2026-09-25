using Aitm.Server.Data;
using Microsoft.Data.Sqlite;
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

// The orchestration steps of init.mjs, ported as InitFull.RunFull ("aitm init --full", RESTRUCTURE.md
// section 2.3, slice 23). Everything runs against a temp workspace and a temp store; nothing here ever
// touches ~/.aitm, a live store, or a real repo.
public class InitFullRunFullTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("aitm-initfull-ws-").FullName;
    private readonly string _home = Directory.CreateTempSubdirectory("aitm-initfull-home-").FullName;
    private readonly string _storeDir = Directory.CreateTempSubdirectory("aitm-initfull-store-").FullName;

    public void Dispose()
    {
        Directory.Delete(_workspace, recursive: true);
        Directory.Delete(_home, recursive: true);
        Directory.Delete(_storeDir, recursive: true);
    }

    private InitFullOptions Options(bool skipChat = true) => new(
        Root: _workspace,
        Instance: "initfull-test",
        DbPath: Path.Combine(_storeDir, "aitm.db"),
        HomeDir: _home,
        SkipChat: skipChat);

    private static void MakeProject(string dir, string manifest)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, manifest), manifest == "package.json" ? "{}" : "x");
    }

    private List<(string Name, string Root)> ReadProjects()
    {
        using SqliteConnection connection = new($"Data Source={Path.Combine(_storeDir, "aitm.db")};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name, root FROM projects ORDER BY name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string, string)> rows = [];
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    [Fact]
    public void RegistersEveryDiscoveredProjectUnderTheSameNamesInitMjsWouldGive()
    {
        MakeProject(Path.Combine(_workspace, "web-app"), "package.json");
        MakeProject(Path.Combine(_workspace, "api"), "composer.json");

        InitFullResult result = InitFull.RunFull(Options());

        Assert.True(result.Success);
        List<(string Name, string Root)> projects = ReadProjects();
        Assert.Contains(projects, p => p.Name == "web-app");
        Assert.Contains(projects, p => p.Name == "api");
    }

    [Fact]
    public void RunsEveryStepInOrder()
    {
        MakeProject(Path.Combine(_workspace, "web-app"), "package.json");

        InitFullResult result = InitFull.RunFull(Options());

        Assert.True(result.Success);
        string[] markers = ["[1] ", "[2] ", "[3] ", "[4] ", "[5] ", "[6] ", "[7] ", "[8] "];
        int[] positions = markers
            .Select(marker => result.Log.IndexOf(marker, StringComparison.Ordinal))
            .ToArray();
        Assert.All(positions, p => Assert.True(p >= 0));
        for (int i = 1; i < positions.Length; i++) Assert.True(positions[i] > positions[i - 1]);
    }

    [Fact]
    public void SkipsPastConversationsWhenAsked()
    {
        InitFullResult result = InitFull.RunFull(Options(skipChat: true));

        Assert.True(result.Success);
        Assert.Contains("skipped", result.Log);
    }

    [Fact]
    public void ReportsNoMemoryDirectoryWhenNoneExistsForThisRepo()
    {
        InitFullResult result = InitFull.RunFull(Options());

        Assert.Contains("no memory directory for this repo", result.Log);
    }

    [Fact]
    public void ARunningASecondTimeIsIdempotent()
    {
        MakeProject(Path.Combine(_workspace, "web-app"), "package.json");
        File.WriteAllText(Path.Combine(_workspace, "web-app", "widget.ts"), "export class Widget {}\n");

        InitFull.RunFull(Options());
        List<(string Name, string Root)> firstRun = ReadProjects();

        InitFullResult second = InitFull.RunFull(Options());
        List<(string Name, string Root)> secondRun = ReadProjects();

        Assert.True(second.Success);
        Assert.Equal(firstRun, secondRun);

        using SqliteConnection connection = new($"Data Source={Path.Combine(_storeDir, "aitm.db")};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM edges";
        Assert.True((long)(command.ExecuteScalar() ?? 0L) > 0);
    }

    [Fact]
    public void AFailingStepReportsFailureAndStopsTheRun()
    {
        // A dbPath whose directory cannot be created (a file sitting where a directory is needed)
        // makes the very first step - opening the store - fail, the way init.mjs's own CLI-build
        // failure (init.mjs:43-46) stops the run and reports it rather than pressing on.
        string blocker = Path.Combine(_storeDir, "blocked");
        File.WriteAllText(blocker, "not a directory");
        InitFullOptions options = Options() with { DbPath = Path.Combine(blocker, "aitm.db") };

        InitFullResult result = InitFull.RunFull(options);

        Assert.False(result.Success);
    }
}
