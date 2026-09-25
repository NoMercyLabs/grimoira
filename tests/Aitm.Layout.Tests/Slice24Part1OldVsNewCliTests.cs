using System.Text.RegularExpressions;
using Aitm.TestSupport;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 24, CLI lane part 1: proves the 15 Aitm.Store/Aitm.Facts verbs behave exactly as
// they did before the wiring, by running each one on a fresh temp instance against the frozen "before"
// binary (OldVsNewCli.OracleDll, built from the commit slice 24 started at) and against today's
// bin-cli/aitm.dll, and diffing stdout, stderr and exit code. Every wired verb gets at least a
// no-arg/help or normal run, and most also get a deliberate error case.
public class Slice24Part1OldVsNewCliTests
{
    // Stats/init/import/backup echo the instance name and/or db path in their own output, which is
    // necessarily different between the two fresh instances this test uses (one per binary, so neither
    // run can see the other's data) — normalized out before the two outputs are compared.
    // query prints its own elapsed time and history prints the mutation's real timestamp — both vary
    // run to run by design, on the SAME binary, never mind two different ones, so neither is part of
    // what "identical behaviour" means here.
    private static string StripVolatile(string s) =>
        Regex.Replace(Regex.Replace(s, @"\(\d+[.,]\d+ms\)", "(<ms>)"),
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z", "<ts>");

    private static (string stdout, string stderr, int exitCode) RunNormalized(
        OldVsNewCli.Result result, string instance, string dbPath)
    {
        string NormalizeOne(string s) => StripVolatile(s.Replace(instance, "<instance>").Replace(dbPath, "<db>"));
        return (NormalizeOne(result.Stdout), NormalizeOne(result.Stderr), result.ExitCode);
    }

    private static void AssertParity(string[] setup, string command)
    {
        string oldInstance = AitmCliRunner.NewTestInstance("p1o");
        string newInstance = AitmCliRunner.NewTestInstance("p1n");
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

            (string oStdout, string oStderr, int oExit) =
                RunNormalized(oldResult, oldInstance, AitmCliRunner.InstanceDbPath(oldInstance));
            (string nStdout, string nStderr, int nExit) =
                RunNormalized(newResult, newInstance, AitmCliRunner.InstanceDbPath(newInstance));

            Assert.Equal(oStdout, nStdout);
            Assert.Equal(oStderr, nStderr);
            Assert.Equal(oExit, nExit);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    public static IEnumerable<object[]> Scenarios()
    {
        (string name, string[] setup, string command)[] cases =
        [
            // init — bare run is its only shape.
            ("init-bare", [], "init"),

            // stats — bare run against an empty-but-initialized store.
            ("stats-empty", ["init"], "stats"),

            // history — no-arg (missing search term is still a valid, empty LIKE '%%') and a seeded hit.
            ("history-empty", ["init"], "history nothing-logged-yet"),
            ("history-hit", ["init", "add --term hist-term --value v1 --category manual"], "history hist-term"),

            // add — normal add, then an error case: an invalid --provenance value.
            ("add-normal", ["init"], "add --term added-term --value v --category manual --provenance stated"),
            ("add-bad-provenance", ["init"], "add --term x --provenance not-a-real-provenance"),

            // query — a seeded confident hit and a no-arg gap.
            ("query-hit", ["init", "add --term query-term --value the-value --category manual"], "query query-term"),
            ("query-gap", ["init"], "query nothing-ever-matches-this"),

            // shed-fact — normal delete and the not-found error path.
            ("shed-fact-hit", ["init", "add --term to-shed --value v --category manual"], "shed-fact --key to-shed"),
            ("shed-fact-miss", ["init"], "shed-fact --key never-existed"),

            // todo / todos / done
            ("todo-normal", ["init"], "todo a todo title --why because"),
            ("todos-empty", ["init"], "todos"),
            ("todos-listed", ["init", "todo first todo"], "todos"),
            ("done-miss", ["init"], "done 999"),
            ("done-hit", ["init", "todo closeable todo"], "done 1"),

            // finding / findings / resolve
            ("finding-normal", ["init"], "finding a finding title --detail d --source s"),
            ("findings-empty", ["init"], "findings"),
            ("findings-listed", ["init", "finding first finding"], "findings"),
            ("resolve-miss", ["init"], "resolve 999"),
            ("resolve-hit", ["init", "finding resolvable finding"], "resolve 1"),
        ];
        foreach ((string name, string[] setup, string command) in cases)
            yield return new object[] { name, setup, command };
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void OldAndNewCliAgree(string _, string[] setup, string command) => AssertParity(setup, command);

    // import needs a second store to import FROM, so it gets its own fixture rather than a shared setup list.
    [Fact]
    public void ImportMatchesOldBehaviourOnANormalMerge()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("p1o-import");
        string newInstance = AitmCliRunner.NewTestInstance("p1n-import");
        string oldFrom = AitmCliRunner.NewTestInstance("p1o-import-src");
        string newFrom = AitmCliRunner.NewTestInstance("p1n-import-src");
        try
        {
            string oldDll = OldVsNewCli.OracleDll();
            string newDll = OldVsNewCli.BinCliDll();

            OldVsNewCli.Run(oldDll, oldInstance, "init");
            OldVsNewCli.Run(newDll, newInstance, "init");
            OldVsNewCli.Run(oldDll, oldFrom, "init");
            OldVsNewCli.Run(oldDll, oldFrom, "add --term imported-term --value v --category manual");
            OldVsNewCli.Run(newDll, newFrom, "init");
            OldVsNewCli.Run(newDll, newFrom, "add --term imported-term --value v --category manual");

            string oldFromDb = AitmCliRunner.InstanceDbPath(oldFrom);
            string newFromDb = AitmCliRunner.InstanceDbPath(newFrom);

            OldVsNewCli.Result oldResult = OldVsNewCli.Run(oldDll, oldInstance, $"import --from \"{oldFromDb}\"");
            OldVsNewCli.Result newResult = OldVsNewCli.Run(newDll, newInstance, $"import --from \"{newFromDb}\"");

            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            string NormalizeOld(string s) => s.Replace(oldInstance, "<i>").Replace(oldDb, "<db>").Replace(oldFromDb, "<from>");
            string NormalizeNew(string s) => s.Replace(newInstance, "<i>").Replace(newDb, "<db>").Replace(newFromDb, "<from>");

            Assert.Equal(NormalizeOld(oldResult.Stdout), NormalizeNew(newResult.Stdout));
            Assert.Equal(NormalizeOld(oldResult.Stderr), NormalizeNew(newResult.Stderr));
            Assert.Equal(oldResult.ExitCode, newResult.ExitCode);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
            AitmCliRunner.DeleteInstance(oldFrom);
            AitmCliRunner.DeleteInstance(newFrom);
        }
    }

    [Fact]
    public void ImportMatchesOldBehaviourOnAMissingFromFlag() => AssertParity(["init"], "import");

    // add --why — the message text doesn't carry the flag, so parity here needs the mutation-log row
    // itself, not just stdout. Oracle: aitm.cs's AddCmd() (aitm.cs:2417) logs GetFlag("--why") ?? "manual".
    [Fact]
    public void AddLogsTheWhyFlagToTheMutationLogLikeTheOldCli()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("p1o-why");
        string newInstance = AitmCliRunner.NewTestInstance("p1n-why");
        try
        {
            string oldDll = OldVsNewCli.OracleDll();
            string newDll = OldVsNewCli.BinCliDll();
            OldVsNewCli.Run(oldDll, oldInstance, "init");
            OldVsNewCli.Run(newDll, newInstance, "init");

            OldVsNewCli.Run(oldDll, oldInstance,
                "add --term why-fixture --value one --category manual --provenance stated --why \"reason X\"");
            OldVsNewCli.Run(newDll, newInstance,
                "add --term why-fixture --value one --category manual --provenance stated --why \"reason X\"");

            Assert.Equal("reason X", ReadWhy(AitmCliRunner.InstanceDbPath(oldInstance)));
            Assert.Equal("reason X", ReadWhy(AitmCliRunner.InstanceDbPath(newInstance)));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    private static string? ReadWhy(string dbPath)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT why FROM mutations WHERE k='why-fixture' ORDER BY id DESC LIMIT 1";
        return select.ExecuteScalar() as string;
    }

    [Fact]
    public void BackupMatchesOldBehaviourWithAnExplicitDestination()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("p1o-backup");
        string newInstance = AitmCliRunner.NewTestInstance("p1n-backup");
        string oldDest = Path.Combine(Path.GetTempPath(), "aitm-slice24-backup-old-" + Guid.NewGuid().ToString("N") + ".db");
        string newDest = Path.Combine(Path.GetTempPath(), "aitm-slice24-backup-new-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            string oldDll = OldVsNewCli.OracleDll();
            string newDll = OldVsNewCli.BinCliDll();
            OldVsNewCli.Run(oldDll, oldInstance, "init");
            OldVsNewCli.Run(newDll, newInstance, "init");
            OldVsNewCli.Run(oldDll, oldInstance, "add --term backed-up --value v --category manual");
            OldVsNewCli.Run(newDll, newInstance, "add --term backed-up --value v --category manual");

            OldVsNewCli.Result oldResult = OldVsNewCli.Run(oldDll, oldInstance, $"backup --to \"{oldDest}\"");
            OldVsNewCli.Result newResult = OldVsNewCli.Run(newDll, newInstance, $"backup --to \"{newDest}\"");

            Assert.Equal(oldResult.Stdout.Replace(oldDest, "<dest>"), newResult.Stdout.Replace(newDest, "<dest>"));
            Assert.Equal(oldResult.Stderr, newResult.Stderr);
            Assert.Equal(oldResult.ExitCode, newResult.ExitCode);
            Assert.True(File.Exists(oldDest));
            Assert.True(File.Exists(newDest));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
            if (File.Exists(oldDest)) File.Delete(oldDest);
            if (File.Exists(newDest)) File.Delete(newDest);
        }
    }

    // backup twice to the SAME --to path — the old wal-checkpoint-then-File.Copy(overwrite: true)
    // always replaced an existing destination; VACUUM INTO refuses to write over one, so BackupTool has
    // to build into a temp file and swap it into place to keep that behaviour byte-for-byte.
    [Fact]
    public void BackupOverwritesAnExistingDestinationLikeTheOldCli()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("p1o-backup-twice");
        string newInstance = AitmCliRunner.NewTestInstance("p1n-backup-twice");
        string oldDest = Path.Combine(Path.GetTempPath(), "aitm-slice24-backup-twice-old-" + Guid.NewGuid().ToString("N") + ".db");
        string newDest = Path.Combine(Path.GetTempPath(), "aitm-slice24-backup-twice-new-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            string oldDll = OldVsNewCli.OracleDll();
            string newDll = OldVsNewCli.BinCliDll();
            OldVsNewCli.Run(oldDll, oldInstance, "init");
            OldVsNewCli.Run(newDll, newInstance, "init");
            OldVsNewCli.Run(oldDll, oldInstance, "add --term backed-up-first --value v --category manual");
            OldVsNewCli.Run(newDll, newInstance, "add --term backed-up-first --value v --category manual");

            OldVsNewCli.Run(oldDll, oldInstance, $"backup --to \"{oldDest}\"");
            OldVsNewCli.Run(newDll, newInstance, $"backup --to \"{newDest}\"");

            OldVsNewCli.Run(oldDll, oldInstance, "add --term backed-up-second --value v --category manual");
            OldVsNewCli.Run(newDll, newInstance, "add --term backed-up-second --value v --category manual");

            OldVsNewCli.Result oldResult = OldVsNewCli.Run(oldDll, oldInstance, $"backup --to \"{oldDest}\"");
            OldVsNewCli.Result newResult = OldVsNewCli.Run(newDll, newInstance, $"backup --to \"{newDest}\"");

            Assert.Equal(oldResult.Stdout.Replace(oldDest, "<dest>"), newResult.Stdout.Replace(newDest, "<dest>"));
            Assert.Equal(oldResult.Stderr, newResult.Stderr);
            Assert.Equal(oldResult.ExitCode, newResult.ExitCode);
            Assert.Equal(0, oldResult.ExitCode);
            Assert.True(File.Exists(oldDest));
            Assert.True(File.Exists(newDest));
            AssertIntegrityOk(newDest);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
            if (File.Exists(oldDest)) File.Delete(oldDest);
            if (File.Exists(newDest)) File.Delete(newDest);
        }
    }

    private static void AssertIntegrityOk(string dbPath)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", (string)check.ExecuteScalar()!);
    }

    // index-packages walks a real directory, so it gets its own fixture to build one.
    [Fact]
    public void IndexPackagesMatchesOldBehaviourOnARealPackageJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "aitm-slice24-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"),
            """{"name":"slice24-fixture-pkg","version":"1.2.3","description":"a fixture package"}""");
        try
        {
            AssertParity(["init"], $"index-packages --root \"{root}\"");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IndexPackagesMatchesOldBehaviourOnAMissingRoot() =>
        AssertParity(["init"], "index-packages --root \"/no/such/directory/aitm-slice24-fixture\"");
}
