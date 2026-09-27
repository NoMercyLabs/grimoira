using Grimora.Graph.Schema;
using Grimora.Store.Data;
using Grimora.Store.Schema;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Layout.Tests;

// RESTRUCTURE.md slice 24, CLI lane part 3: proves the 12 Grimora.Graph verbs behave exactly as they did
// before the wiring, by running each one on a fresh temp instance against the frozen "before" binary
// (OldVsNewCli.OracleDll, built from the commit slice 24 started at) and against today's bin-cli-old/grimora.dll,
// and diffing stdout, stderr and exit code. None of these 12 verbs print a timestamp or an elapsed time,
// so — unlike part 1's query/history/stats — no output needs normalizing before the comparison.
public class GraphCliParityTests
{
    private static void AssertParity(string[] setup, string command)
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("p3o");
        string newInstance = GrimoraCliRunner.NewTestInstance("p3n");
        try
        {
            string oldDll = OldVsNewCli.OracleDll();
            string newDll = OldVsNewCli.BinCliDll();
            foreach (string s in setup)
            {
                OldVsNewCli.Run(oldDll, oldInstance, s);
                OldVsNewCli.Run(newDll, newInstance, s);
            }

            OldVsNewCli.Result oldResult = OldVsNewCli.Run(oldDll, oldInstance, command);
            OldVsNewCli.Result newResult = OldVsNewCli.Run(newDll, newInstance, command);

            Assert.Equal(oldResult.Stdout, newResult.Stdout);
            Assert.Equal(oldResult.Stderr, newResult.Stderr);
            Assert.Equal(oldResult.ExitCode, newResult.ExitCode);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    // A spine.json shared by seed-edges, impact, graph-query, graph-path and graph-explain — one fixture
    // rather than five, since it only needs to exist on disk and never changes. Its file paths carry a
    // hyphen, a dot and a slash ("src/api/widget-controller.cs"), covering the card's "path with a
    // hyphen, dot and slash" requirement for the verbs that take one.
    private static string CreateSpineFixture()
    {
        string path = Path.Combine(Path.GetTempPath(), "grimora-slice24-p3-spine-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
        {
          "edges": [
            { "symbol": "FixtureWidget", "contract": "decl", "project": "web", "file": "src/Widget.ts", "line": 10, "usage": "", "hardcoded": 0 },
            { "symbol": "FixtureWidget", "contract": "", "project": "server", "file": "src/api/widget-controller.cs", "line": 42, "usage": "new FixtureWidget()", "hardcoded": 1 },
            { "symbol": "FixtureWidget", "contract": "", "project": "server", "file": "src/api/widget-service.cs", "line": 7, "usage": "FixtureWidget.Create()", "hardcoded": 0 }
          ]
        }
        """);
        return path;
    }

    // A real fixture repo for extract-edges/candidates/promote/promote-all, since those verbs walk a
    // real directory. The project name ("promote-fixture.v1") carries a hyphen and a dot, and its root
    // is a temp path (a slash), covering the card's hyphen/dot/slash requirement for these verbs too.
    private static string CreateExtractFixtureRepo()
    {
        string root = Path.Combine(Path.GetTempPath(), "grimora-slice24-p3-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "PromoteOneSymbol.cs"), "class C { void M() { PromoteOneSymbol(); } }");
        File.WriteAllText(Path.Combine(root, "PromoteAllSymbolA.cs"), "class A { void M() { PromoteAllSymbol(); } }");
        File.WriteAllText(Path.Combine(root, "PromoteAllSymbolB.cs"), "class B { void M() { PromoteAllSymbol(); } }");
        return root;
    }

    // A --root value that never has to exist on disk (project only stores the string), but still has to
    // be a portable stand-in for "some path with a slash in it": a hardcoded "/tmp/..." literal is a real
    // directory shape on Linux and nowhere in particular on Windows, so CliGoldens.Canonical's own
    // <TEMP>-token collapse (built from this machine's real Path.GetTempPath()) either fires or doesn't
    // depending only on which OS happens to be replaying the golden, desyncing the golden lookup. Building
    // it from Path.GetTempPath() the same way CreateExtractFixtureRepo does makes both freeze and replay
    // collapse it to <TEMP> identically, on every OS.
    private static readonly string TempWebRoot =
        Path.Combine(Path.GetTempPath(), "grimora-slice24-p3-web").Replace('\\', '/');

    private static readonly string TempHyphenDotSlashRoot =
        Path.Combine(Path.GetTempPath(), "some-dir", "sub.v2").Replace('\\', '/');

    public static IEnumerable<object[]> Scenarios()
    {
        (string name, string[] setup, string command)[] cases =
        [
            // project — bare/help-shaped (missing --name is an error), a normal registration, one whose
            // --name and --root carry a hyphen, a dot and a slash, and a missing-required-flag error.
            ("project-bare", ["init"], "project"),
            ("project-normal", ["init"], $"project --name web --root {TempWebRoot} --lang ts"),
            ("project-hyphen-dot-slash", ["init"], $"project --name my-proj.v2 --root {TempHyphenDotSlashRoot} --lang ts"),
            ("project-missing-root", ["init"], "project --name onlyname"),

            // projects — empty and listed.
            ("projects-empty", ["init"], "projects"),
            ("projects-listed", ["init", $"project --name web --root {TempWebRoot} --lang ts"], "projects"),

            // forget-project — a name that was never registered (0 edges dropped) and a missing --name error.
            ("forget-project-miss", ["init"], "forget-project --name never-registered"),
            ("forget-project-missing-name", ["init"], "forget-project"),

            // impact — a symbol with no recorded consumers (query alone; the seeded/hit case has its own
            // dedicated test below, since it needs the shared spine fixture).
            ("impact-none", ["init"], "impact NoSuchSymbolAtAll"),

            // graph-query — a question that matches nothing.
            ("graph-query-gap", ["init"], "graph-query nothing-ever-matches-this-either"),

            // graph-path — the built-in usage guard (fewer than 2 positionals) and an unresolvable name.
            ("graph-path-bare", ["init"], "graph-path"),
            ("graph-path-one-arg", ["init"], "graph-path OnlyOne"),
            ("graph-path-unresolvable", ["init"], "graph-path NoSuchSymbolAtAll AlsoNoSuchSymbol"),

            // graph-explain — a blank symbol (usage guard) and a symbol that matches nothing.
            ("graph-explain-blank", ["init"], "graph-explain"),
            ("graph-explain-none", ["init"], "graph-explain NoSuchSymbolAtAll"),

            // candidates — nothing staged yet.
            ("candidates-empty", ["init"], "candidates"),

            // promote — no pending candidate with that id.
            ("promote-miss", ["init"], "promote 999"),

            // promote-all — no pending candidates for that symbol.
            ("promote-all-miss", ["init"], "promote-all --symbol NoSuchPendingSymbol"),
        ];
        foreach ((string name, string[] setup, string command) in cases)
            yield return [name, setup, command];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void OldAndNewCliAgree(string _, string[] setup, string command) => AssertParity(setup, command);

    // seed-edges — a normal load from a real spine file, and the missing-file error path (the tool
    // returns the same "no spine file at ..." text the old inline function wrote to stderr; the grimora.cs
    // case has to route it to stderr the same way for this to hold).
    [Fact]
    public void SeedEdgesMatchesOldBehaviourOnANormalLoad()
    {
        string spine = CreateSpineFixture();
        try
        {
            AssertParity(["init"], $"seed-edges --from \"{spine}\"");
        }
        finally
        {
            File.Delete(spine);
        }
    }

    [Fact]
    public void SeedEdgesMatchesOldBehaviourOnAMissingSpineFile() =>
        AssertParity(["init"], "seed-edges --from \"/no/such/directory/grimora-slice24-p3-fixture-spine.json\"");

    // impact/graph-query/graph-path/graph-explain on real seeded data, via the shared spine fixture.
    [Fact]
    public void ImpactMatchesOldBehaviourOnASeededHit()
    {
        string spine = CreateSpineFixture();
        try
        {
            AssertParity(["init", $"seed-edges --from \"{spine}\""], "impact FixtureWidget");
        }
        finally
        {
            File.Delete(spine);
        }
    }

    [Fact]
    public void GraphQueryMatchesOldBehaviourOnASeededHit()
    {
        string spine = CreateSpineFixture();
        try
        {
            AssertParity(["init", $"seed-edges --from \"{spine}\""], "graph-query widget");
        }
        finally
        {
            File.Delete(spine);
        }
    }

    // The path endpoint carries a hyphen, a dot and a slash ("src/api/widget-controller.cs"), resolved
    // by its filename tail — exercising ResolveGraphNode's file branch, not just its symbol branch.
    [Fact]
    public void GraphPathMatchesOldBehaviourFromASymbolToAHyphenatedFilePath()
    {
        string spine = CreateSpineFixture();
        try
        {
            AssertParity(["init", $"seed-edges --from \"{spine}\""], "graph-path FixtureWidget widget-controller.cs");
        }
        finally
        {
            File.Delete(spine);
        }
    }

    [Fact]
    public void GraphExplainMatchesOldBehaviourOnASeededHit()
    {
        string spine = CreateSpineFixture();
        try
        {
            AssertParity(["init", $"seed-edges --from \"{spine}\""], "graph-explain FixtureWidget");
        }
        finally
        {
            File.Delete(spine);
        }
    }

    // extract-edges — no projects registered yet, then a normal scan of a real fixture repo.
    [Fact]
    public void ExtractEdgesMatchesOldBehaviourWithNoProjectsRegistered() =>
        AssertParity(["init"], "extract-edges --symbol WhateverSymbol");

    [Fact]
    public void ExtractEdgesMatchesOldBehaviourOnARealFixtureRepo()
    {
        string root = CreateExtractFixtureRepo();
        try
        {
            AssertParity(
                ["init", $"project --name promote-fixture.v1 --root \"{root}\" --globs \"*.cs\""],
                "extract-edges --symbol PromoteOneSymbol");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // candidates — listed after a real extraction.
    [Fact]
    public void CandidatesMatchesOldBehaviourAfterARealExtraction()
    {
        string root = CreateExtractFixtureRepo();
        try
        {
            AssertParity(
                [
                    "init",
                    $"project --name promote-fixture.v1 --root \"{root}\" --globs \"*.cs\"",
                    "extract-edges --symbol PromoteOneSymbol",
                ],
                "candidates --symbol PromoteOneSymbol");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // promote — the pending candidate from a real extraction promotes the same way on both binaries.
    // Both instances run the identical setup sequence, so the candidate gets the same id (1) on each.
    [Fact]
    public void PromoteMatchesOldBehaviourOnARealPendingCandidate()
    {
        string root = CreateExtractFixtureRepo();
        try
        {
            AssertParity(
                [
                    "init",
                    $"project --name promote-fixture.v1 --root \"{root}\" --globs \"*.cs\"",
                    "extract-edges --symbol PromoteOneSymbol",
                ],
                "promote 1");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // promote-all — bulk-replaces the pending set for a symbol with two real candidates.
    [Fact]
    public void PromoteAllMatchesOldBehaviourOnARealPendingSet()
    {
        string root = CreateExtractFixtureRepo();
        try
        {
            AssertParity(
                [
                    "init",
                    $"project --name promote-fixture.v1 --root \"{root}\" --globs \"*.cs\"",
                    "extract-edges --symbol PromoteAllSymbol",
                ],
                "promote-all --symbol PromoteAllSymbol");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // RESTRUCTURE.md section 5: forget-project and promote-all are named as CLI verbs that delete or
    // bulk-change and so must back up first. The oracle (frozen before this slice) never did — its case
    // body is two bare DELETEs — so ForgetProjectTool/PromoteAllTool taking a backup first is a real,
    // deliberate behaviour difference, not a parity bug: it changes a side effect (a new file appears
    // under the instance's backups/ dir) without changing a byte of stdout/stderr/exit code, which the
    // scenarios above already prove. These two tests prove the new side effect actually happens.
    [Fact]
    public void ForgetProjectBacksUpFirstOnTheNewBinaryUnlikeTheOldOne()
    {
        string newInstance = GrimoraCliRunner.NewTestInstance("p3n-fp-backup");
        try
        {
            string newDll = OldVsNewCli.BinCliDll();
            OldVsNewCli.Run(newDll, newInstance, "init");
            OldVsNewCli.Run(newDll, newInstance, $"project --name web --root {TempWebRoot} --lang ts");
            string backupsDir = Path.Combine(GrimoraCliRunner.InstanceDir(newInstance), "backups");
            Assert.False(Directory.Exists(backupsDir));

            OldVsNewCli.Run(newDll, newInstance, "forget-project --name web");
            Assert.True(Directory.Exists(backupsDir));
            Assert.NotEmpty(Directory.GetFiles(backupsDir));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    // RESTRUCTURE.md slice 31/31b: edges.file_rel and the reader-side fallback in ImpactTool,
    // GraphQueryTool, GraphPathTool and GraphExplainTool must give the same answer whether the store
    // has never seen file_rel (the oracle, frozen before slice 24, never will) or has already been
    // migrated by IndexCodeTool with the project root unmoved (the absolute path is identical either
    // way). Each case: seed identical data on both binaries, migrate ONLY the new instance's store in
    // place (mirroring what IndexCodeTool does, without going through a CLI verb — index-code has none),
    // then diff the read verb's stdout/stderr/exit code same as every other parity case in this file.
    private static string CreateMigratedRootFixture(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "grimora-slice31-mig-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string widget = Path.Combine(root, "widget.ts").Replace('\\', '/');
        string controller = Path.Combine(root, "src", "api", "widget-controller.cs").Replace('\\', '/');
        string service = Path.Combine(root, "src", "api", "widget-service.cs").Replace('\\', '/');
        string path = Path.Combine(Path.GetTempPath(), "grimora-slice31-mig-spine-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, $$"""
        {
          "edges": [
            { "symbol": "FixtureWidget", "contract": "decl", "project": "web", "file": "{{widget}}", "line": 10, "usage": "", "hardcoded": 0 },
            { "symbol": "FixtureWidget", "contract": "", "project": "web", "file": "{{controller}}", "line": 42, "usage": "new FixtureWidget()", "hardcoded": 1 },
            { "symbol": "FixtureWidget", "contract": "", "project": "web", "file": "{{service}}", "line": 7, "usage": "FixtureWidget.Create()", "hardcoded": 0 }
          ]
        }
        """);
        return path;
    }

    // Applies GraphFileRelSchema and its backfill directly to the new instance's own db file — the same
    // migration IndexCodeTool runs — leaving the old instance's store untouched (no file_rel, ever).
    private static void MigrateNewInstanceInPlace(string newInstance)
    {
        string dbPath = GrimoraCliRunner.InstanceDbPath(newInstance);
        string backupDir = Path.Combine(GrimoraCliRunner.InstanceDir(newInstance), "backups");
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        SchemaRunResultOrThrow(SchemaRunner.Run(connection, [new GraphFileRelSchema(connection)], backupDir));
        GraphFileRelSchema.Backfill(connection);
    }

    private static void SchemaRunResultOrThrow(SchemaRunResult result)
    {
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }

    private static void AssertParityOnAMigratedStore(string command)
    {
        string spine = CreateMigratedRootFixture(out string root);
        string oldInstance = GrimoraCliRunner.NewTestInstance("p3o-mig");
        string newInstance = GrimoraCliRunner.NewTestInstance("p3n-mig");
        try
        {
            string oldDll = OldVsNewCli.OracleDll();
            string newDll = OldVsNewCli.BinCliDll();
            string[] setup =
            [
                "init",
                $"project --name web --root \"{root}\" --lang ts",
                $"seed-edges --from \"{spine}\"",
            ];
            foreach (string s in setup)
            {
                OldVsNewCli.Run(oldDll, oldInstance, s);
                OldVsNewCli.Run(newDll, newInstance, s);
            }

            MigrateNewInstanceInPlace(newInstance);

            OldVsNewCli.Result oldResult = OldVsNewCli.Run(oldDll, oldInstance, command);
            OldVsNewCli.Result newResult = OldVsNewCli.Run(newDll, newInstance, command);

            Assert.Equal(oldResult.Stdout, newResult.Stdout);
            Assert.Equal(oldResult.Stderr, newResult.Stderr);
            Assert.Equal(oldResult.ExitCode, newResult.ExitCode);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
            File.Delete(spine);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImpactMatchesOldBehaviourOnAMigratedStoreWithRootUnmoved() =>
        AssertParityOnAMigratedStore("impact FixtureWidget");

    [Fact]
    public void GraphQueryMatchesOldBehaviourOnAMigratedStoreWithRootUnmoved() =>
        AssertParityOnAMigratedStore("graph-query widget");

    [Fact]
    public void GraphPathMatchesOldBehaviourOnAMigratedStoreWithRootUnmoved() =>
        AssertParityOnAMigratedStore("graph-path FixtureWidget widget-controller.cs");

    [Fact]
    public void GraphExplainMatchesOldBehaviourOnAMigratedStoreWithRootUnmoved() =>
        AssertParityOnAMigratedStore("graph-explain FixtureWidget");

    [Fact]
    public void PromoteAllBacksUpFirstOnTheNewBinaryUnlikeTheOldOne()
    {
        string newInstance = GrimoraCliRunner.NewTestInstance("p3n-pa-backup");
        string root = CreateExtractFixtureRepo();
        try
        {
            string newDll = OldVsNewCli.BinCliDll();
            OldVsNewCli.Run(newDll, newInstance, "init");
            OldVsNewCli.Run(newDll, newInstance, $"project --name promote-fixture.v1 --root \"{root}\" --globs \"*.cs\"");
            OldVsNewCli.Run(newDll, newInstance, "extract-edges --symbol PromoteAllSymbol");
            string backupsDir = Path.Combine(GrimoraCliRunner.InstanceDir(newInstance), "backups");
            Assert.False(Directory.Exists(backupsDir));

            OldVsNewCli.Run(newDll, newInstance, "promote-all --symbol PromoteAllSymbol");
            Assert.True(Directory.Exists(backupsDir));
            Assert.NotEmpty(Directory.GetFiles(backupsDir));
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(newInstance);
            Directory.Delete(root, recursive: true);
        }
    }
}
