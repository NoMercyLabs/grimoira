using System.Text.RegularExpressions;
using Aitm.TestSupport;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 24, CLI lane part 4a: proves the 10 Brain-read sub-verbs (nested under `brain`,
// plus the bare `brain` dispatcher itself) behave exactly as they did before the wiring, by running each
// one on a fresh temp instance against the frozen "before" binary (OldVsNewCli.OracleDll, built from the
// commit slice 24 started at) and against today's bin-cli/aitm.dll, and diffing stdout, stderr and exit
// code. Every wired sub-verb gets at least a bare/error run; the read verbs that need real graph data
// (scope, common, place, why, verify, audit) get a shared fixture seeded through `brain learn` — the old
// binary's own verb, run identically on both instances, exactly as Slice24Part2OldVsNewCliTests seeds
// through index-memory/index-chat/index-docs rather than writing to either store directly.
//
// None of these 10 verbs take a free-text FTS query the way mem/recall/doc do (part 2's hyphen/slash/
// dot/underscore requirement targets that shape of verb): `scope`/`common` take project slugs, `place`
// takes a codekind slug, `why`/`verify` take an exact node key. `place`'s codekind fixture below
// ("fixture-service-a") already carries a hyphen, which is the only punctuation this verb's key shape
// can carry; slash/dot/underscore have no meaning in a node key here (a `:` schema separator is what the
// key format actually uses), so this file does not force artificial coverage of the other three.
public class Slice24Part4aOldVsNewCliTests
{
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
        string oldInstance = AitmCliRunner.NewTestInstance("p4ao");
        string newInstance = AitmCliRunner.NewTestInstance("p4an");
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
            // brain — bare, help, and an unrecognised sub-verb all fall through to the same usage line.
            ("brain-bare", ["init"], "brain"),
            ("brain-help", ["init"], "brain help"),
            ("brain-unknown-subverb", ["init"], "brain totally-bogus-subverb"),

            // core — empty store.
            ("core-empty", ["init"], "brain core"),

            // scope — missing args (usage) and an empty result (unresolvable projects).
            ("scope-missing-args", ["init"], "brain scope"),
            ("scope-unresolved", ["init"], "brain scope nothing-fixture-a nothing-fixture-b"),

            // common — missing args (needs 2+) and an empty result.
            ("common-missing-args", ["init"], "brain common"),
            ("common-one-arg", ["init"], "brain common only-one-fixture"),
            ("common-unresolved", ["init"], "brain common nothing-fixture-a nothing-fixture-b"),

            // place — missing arg (usage) and a codekind nobody has placed yet.
            ("place-missing-arg", ["init"], "brain place"),
            ("place-miss", ["init"], "brain place never-placed-fixture-kind"),

            // why — missing arg (usage) and a node key that does not exist.
            ("why-missing-arg", ["init"], "brain why"),
            ("why-miss", ["init"], "brain why fixture:never-existed"),

            // verify — missing arg (usage) and a node key that does not exist.
            ("verify-missing-arg", ["init"], "brain verify"),
            ("verify-miss", ["init"], "brain verify fixture:never-existed"),

            // stale — empty store, default days and an explicit --days.
            ("stale-empty-default", ["init"], "brain stale"),
            ("stale-empty-days-flag", ["init"], "brain stale --days 7"),

            // gaps — empty store, then one logged via `mem`'s own miss path (the same gap channel
            // `brain gaps` reads, seeded through a verb whose CLI behaviour part 2 already pinned).
            ("gaps-empty", ["init"], "brain gaps"),
            ("gaps-seeded",
                ["init", "mem nothing-ever-matches-this-slice24-gaps-fixture"],
                "brain gaps"),

            // audit — empty store (every section reads 0).
            ("audit-empty", ["init"], "brain audit"),

            // export — default --to path (under the instance root, normalized like every other path).
            ("export-default", ["init"], "brain export"),
        ];
        foreach ((string name, string[] setup, string command) in cases)
            yield return new object[] { name, setup, command };
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void OldAndNewCliAgree(string _, string[] setup, string command) => AssertParity(setup, command);

    // scope, common, place, why, verify and audit all need real graph data to exercise their non-empty
    // path. One shared fixture built entirely through `brain learn` (unchanged by this part, so it seeds
    // both binaries identically), then each read verb runs against it.
    [Fact]
    public void ScopeCommonPlaceWhyVerifyAndAuditMatchOldBehaviourOnARealFixture()
    {
        string[] seed =
        [
            "init",
            "brain learn node proj:svc-a-fixture project \"Service A Fixture\"",
            "brain learn node proj:svc-b-fixture project \"Service B Fixture\"",
            "brain learn node seam:shared-fixture seam \"Shared Fixture Seam\"",
            "brain learn triple proj:svc-a-fixture consumes seam:shared-fixture --because fixture",
            "brain learn triple proj:svc-b-fixture consumes seam:shared-fixture --because fixture",
            "brain learn node kind:fixture-service-a codekind \"Fixture Service A\"",
            // No --facet override: aitm.cs's own `brain learn slot` case (part 4b's wiring, not this
            // part's) currently ignores --facet and always writes the "text" default — a real bug, but
            // in a verb this part does not own. Leaving --facet off keeps this fixture (which exists
            // only to exercise `place`'s slot read) decoupled from that unrelated defect.
            "brain learn slot kind:fixture-service-a language TypeScript --because fixture",
            "brain learn triple kind:fixture-service-a belongs_in proj:svc-a-fixture --because fixture",
        ];

        AssertParity(seed, "brain scope svc-a-fixture svc-b-fixture");
        AssertParity(seed, "brain common svc-a-fixture svc-b-fixture");
        AssertParity(seed, "brain place fixture-service-a");
        AssertParity(seed, "brain why proj:svc-a-fixture");
        AssertParity(seed, "brain verify proj:svc-a-fixture");
        AssertParity(seed, "brain audit");
        AssertParity(seed, "brain core");
    }
}
