using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 24 bullet 1: "`selftest` is removed. A test counts that all 63 of its checks
// exist in the test projects." aitm.cs's old `SelfTest()` held exactly 63 `Check("<name>", ...)` calls
// (commit history before this slice: aitm.cs:1877-2163). Since `selftest` and its 63 checks are deleted
// in this same slice, the 63 names below are a NAME TABLE copied verbatim from that deleted method — the
// only record of them left. Each name maps to the C# test method that now covers the same behaviour (a
// MAPPING FILE, this class), verified structurally: the mapped file must exist under tests/ and contain
// a `public void <Method>(` declaration. This is a real, permanent regression guard — delete a mapped
// test method (or its file) and this test goes red.
public partial class SelfTestCoverageTests
{
    // Verbatim from aitm.cs's deleted SelfTest, in the order the checks ran.
    public static readonly string[] SelfTestChecks =
    [
        "knowledge: stored fact is retrievable",
        "refusal: nonsense query returns nothing",
        "gaps: refused query is logged once with misses=2",
        "gaps: a covering learn auto-resolves the gap",
        "gaps: non-covering learn resolves nothing new",
        "guard: prose node kind is rejected",
        "guard: paragraph node label is rejected",
        "guard: well-formed node still writes",
        "current: projection reflects latest value only",
        "history: cold log retains both fact mutations",
        "impact: has_more is flagged hardcoded",
        "edges: re-sync is idempotent (no new mutations)",
        "todos: open and done both logged",
        "guard: closing a nonexistent todo is a no-op (no phantom mutation)",
        "recall: indexed chat message is retrievable",
        "isolation: chat never leaks into the verified-facts channel",
        "extract: token match respects word boundaries",
        "promote: candidate enters the curated graph",
        "promote: insert is logged to the cold edge log",
        "promote: candidate is marked promoted",
        "floor: distinctive term still retrieves in a populated corpus",
        "floor: generic low-IDF term is refused once corpus is large",
        "coverage: query whose tokens mostly match retrieves",
        "coverage: 3+ token query matching one tangential word is refused",
        "docs: absorbed section is retrievable",
        "shed-doc: removes the section from the docs channel",
        "tokens: a kebab-case name splits into its words",
        "tokens: a path splits on separators",
        "path terms: folder words become searchable",
        "path terms: generic and numeric segments are dropped",
        "shed-fact: fact is retrievable before shedding",
        "shed-fact: removes the fact from the facts channel",
        "shed-fact: removes the fact from the FTS mirror",
        "memory: stored rule is retrievable by content",
        "memory: hard-rule flag persists for the always-on core",
        "memory: isolated from the verified-facts channel",
        "memory: re-index of an unchanged rule logs no phantom mutation",
        "brain: node retrievable via porter FTS (stemmed)",
        "brain: predicate guard rejects an unknown predicate",
        "brain: dangling subject is a write error, not a silent miss",
        "brain: intersection finds the shared seam in one query",
        "brain: supersede keeps one live node + full history",
        "brain: re-adding an unchanged node is a no-op (no phantom version)",
        "learn: reinforce raises hits + sets recency for a live node",
        "learn: reinforce ignores non-node keys (no junk usage rows)",
        "learn: usage-blended recall ranks the reinforced node above an equal-relevance one",
        "learn: re-asserting a triple strengthens conf without duplicating",
        "recall: hyphenated alias expansion is quoted (no FTS5 syntax crash)",
        "recall: a synonym alias expands the query (hub -> signalr)",
        "recall: substring fallback catches what FTS misses (dataresponse)",
        "merge: source retired, edge re-pointed, no dead reference",
        "forget: node + its edges retired (no orphaned reference)",
        "conflicts: requires+forbids on the same pair is detected",
        "verify: confirmation timestamp recorded",
        "graph-query: finds the fixture symbol",
        "graph-query: shows its declaration site",
        "graph-query: groups the match under its home project",
        "graph-path: finds the 2-hop path",
        "graph-path: reports no path past depth 6",
        "graph-path: same node short-circuits",
        "graph-path: unresolvable name refuses cleanly",
        "graph-explain: reports the declaration kind and site",
        "graph-explain: lists users grouped by project",
    ];

    // Mapping rule: each of the 63 names above maps to the (relative-to-repo-root file, method) that now
    // proves the same behaviour. A method may cover more than one check (e.g. one test asserts both
    // halves of a floor/coverage gate, exactly as the old selftest did in a single Check() line's
    // neighbours) — the map is many-to-one, never many-to-zero.
    private static readonly Dictionary<string, (string File, string Method)> Coverage = new()
    {
        ["knowledge: stored fact is retrievable"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "CliShapeMatchesTodaysCliOutputForAConfidentMatch"),
        ["refusal: nonsense query returns nothing"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "CliShapeReportsNoConfidentAnswerForAGapQuery"),
        ["gaps: refused query is logged once with misses=2"] =
            ("tests/Aitm.Store.Tests/GapLogTests.cs", "ARepeatedMissIncrementsMissesOnTheSameRow"),
        ["gaps: a covering learn auto-resolves the gap"] =
            ("tests/Aitm.Brain.Tests/BrainLearnToolTests.cs", "CliLearnAutoResolvesACoveringGapButNotAnUnrelatedOne"),
        ["gaps: non-covering learn resolves nothing new"] =
            ("tests/Aitm.Brain.Tests/BrainLearnToolTests.cs", "CliLearnAutoResolvesACoveringGapButNotAnUnrelatedOne"),
        ["guard: prose node kind is rejected"] =
            ("tests/Aitm.Brain.Tests/BrainLearnToolTests.cs", "CliGuardRejectsAProseNodeKindAndWritesNothing"),
        ["guard: paragraph node label is rejected"] =
            ("tests/Aitm.Brain.Tests/BrainLearnToolTests.cs", "CliGuardRejectsAParagraphLabelAndWritesNothing"),
        ["guard: well-formed node still writes"] =
            ("tests/Aitm.Brain.Tests/BrainLearnToolTests.cs", "CliWellFormedNodeWritesSilentlyAndLogsTheMutation"),
        ["current: projection reflects latest value only"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "ReAddingTheSameTermUpsertsTheCurrentProjectionToTheLatestValue"),
        ["history: cold log retains both fact mutations"] =
            ("tests/Aitm.Store.Tests/HistoryToolTests.cs", "CliShapeMatchesTodaysCliOutput"),
        ["impact: has_more is flagged hardcoded"] =
            ("tests/Aitm.Graph.Tests/SeedEdgesToolTests.cs", "ASeededEdgeMarkedHardcodedIsFlaggedAndAReSyncAddsNoEdgeMutations"),
        ["edges: re-sync is idempotent (no new mutations)"] =
            ("tests/Aitm.Graph.Tests/SeedEdgesToolTests.cs", "ASeededEdgeMarkedHardcodedIsFlaggedAndAReSyncAddsNoEdgeMutations"),
        ["todos: open and done both logged"] =
            ("tests/Aitm.Facts.Tests/DoneToolTests.cs", "ClosingAnOpenTodoLogsTheUpdateAlongsideTheOriginalInsert"),
        ["guard: closing a nonexistent todo is a no-op (no phantom mutation)"] =
            ("tests/Aitm.Facts.Tests/DoneToolTests.cs", "ClosingANonexistentTodoIsANoOpAndLogsNoPhantomMutation"),
        ["recall: indexed chat message is retrievable"] =
            ("tests/Aitm.Memory.Tests/IndexChatToolTests.cs", "AnIndexedMessageIsRetrievableThroughTheChatFtsMirror"),
        ["isolation: chat never leaks into the verified-facts channel"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "AFactsQueryNeverSurfacesAChatOnlyRow"),
        ["extract: token match respects word boundaries"] =
            ("tests/Aitm.Graph.Tests/ExtractEdgesToolTests.cs", "SymbolMatchRespectsWordBoundariesAndNeverMatchesInsideALongerIdentifier"),
        ["promote: candidate enters the curated graph"] =
            ("tests/Aitm.Graph.Tests/PromoteToolTests.cs", "PromotingAPendingCandidateInsertsTheEdgeLogsItAndMarksTheCandidatePromoted"),
        ["promote: insert is logged to the cold edge log"] =
            ("tests/Aitm.Graph.Tests/PromoteToolTests.cs", "PromotingAPendingCandidateInsertsTheEdgeLogsItAndMarksTheCandidatePromoted"),
        ["promote: candidate is marked promoted"] =
            ("tests/Aitm.Graph.Tests/PromoteToolTests.cs", "PromotingAPendingCandidateInsertsTheEdgeLogsItAndMarksTheCandidatePromoted"),
        ["floor: distinctive term still retrieves in a populated corpus"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "ADistinctiveTermStillRetrievesButAGenericLowIdfTermIsRefusedInAPopulatedCorpus"),
        ["floor: generic low-IDF term is refused once corpus is large"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "ADistinctiveTermStillRetrievesButAGenericLowIdfTermIsRefusedInAPopulatedCorpus"),
        ["coverage: query whose tokens mostly match retrieves"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "ACoverageQueryMostlyMatchingRetrievesButOneTangentialWordIsRefused"),
        ["coverage: 3+ token query matching one tangential word is refused"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "ACoverageQueryMostlyMatchingRetrievesButOneTangentialWordIsRefused"),
        ["docs: absorbed section is retrievable"] =
            ("tests/Aitm.Docs.Tests/DocToolTests.cs", "CliShapeMatchesTodaysCliOutputForAConfidentMatch"),
        ["shed-doc: removes the section from the docs channel"] =
            ("tests/Aitm.Docs.Tests/ShedDocToolTests.cs", "MatchesTodaysCliOutputAndDeletesMatchingSections"),
        ["tokens: a kebab-case name splits into its words"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "AKebabCaseTermIsRetrievableByItsSeparateWords"),
        ["tokens: a path splits on separators"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "APathShapedTermIsRetrievableBySeparatedWords"),
        ["path terms: folder words become searchable"] =
            ("tests/Aitm.Docs.Tests/IndexDocsToolTests.cs", "FolderPathWordsBecomeSearchableTermsWhileGenericAndNumericSegmentsAreDropped"),
        ["path terms: generic and numeric segments are dropped"] =
            ("tests/Aitm.Docs.Tests/IndexDocsToolTests.cs", "FolderPathWordsBecomeSearchableTermsWhileGenericAndNumericSegmentsAreDropped"),
        ["shed-fact: fact is retrievable before shedding"] =
            ("tests/Aitm.Facts.Tests/ShedFactToolTests.cs", "MatchesTodaysCliOutputAndDeletesTheRow"),
        ["shed-fact: removes the fact from the facts channel"] =
            ("tests/Aitm.Facts.Tests/ShedFactToolTests.cs", "MatchesTodaysCliOutputAndDeletesTheRow"),
        ["shed-fact: removes the fact from the FTS mirror"] =
            ("tests/Aitm.Facts.Tests/ShedFactToolTests.cs", "MatchesTodaysCliOutputAndDeletesTheRow"),
        ["memory: stored rule is retrievable by content"] =
            ("tests/Aitm.Memory.Tests/MemToolTests.cs", "CliShapeMatchesTodaysCliOutputForAConfidentMatch"),
        ["memory: hard-rule flag persists for the always-on core"] =
            ("tests/Aitm.Memory.Tests/MemToolTests.cs", "CliShapeListsOnlyHardRulesWhenHardIsSet"),
        ["memory: isolated from the verified-facts channel"] =
            ("tests/Aitm.Facts.Tests/QueryToolTests.cs", "AFactsQueryNeverSurfacesAMemoryOnlyRow"),
        ["memory: re-index of an unchanged rule logs no phantom mutation"] =
            ("tests/Aitm.Memory.Tests/IndexMemoryToolTests.cs", "ReindexingAnUnchangedDirectoryLogsNoPhantomMutation"),
        ["brain: node retrievable via porter FTS (stemmed)"] =
            ("tests/Aitm.Brain.Tests/BrainRecallToolTests.cs", "CliShapeMatchesTodaysCliOutputForAnFtsHit"),
        ["brain: predicate guard rejects an unknown predicate"] =
            ("tests/Aitm.Brain.Tests/BrainGraphInvariantTests.cs", "PredicateGuardRejectsAnUnknownPredicate"),
        ["brain: dangling subject is a write error, not a silent miss"] =
            ("tests/Aitm.Brain.Tests/BrainGraphInvariantTests.cs", "DanglingSubjectIsAWriteErrorNotASilentMiss"),
        ["brain: intersection finds the shared seam in one query"] =
            ("tests/Aitm.Brain.Tests/BrainGraphInvariantTests.cs", "IntersectionFindsTheSharedSeamInOneQuery"),
        ["brain: supersede keeps one live node + full history"] =
            ("tests/Aitm.Brain.Tests/BrainGraphInvariantTests.cs", "SupersedeOnAChangedNodeKeepsOneLiveRowPlusFullHistory"),
        ["brain: re-adding an unchanged node is a no-op (no phantom version)"] =
            ("tests/Aitm.Brain.Tests/BrainLearnToolTests.cs", "McpShapeReportsNoopWhenTheNodeIsUnchanged"),
        ["learn: reinforce raises hits + sets recency for a live node"] =
            ("tests/Aitm.Brain.Tests/BrainUsageSignalTests.cs", "ReinforceRaisesHitsAndSetsRecencyForALiveNode"),
        ["learn: reinforce ignores non-node keys (no junk usage rows)"] =
            ("tests/Aitm.Brain.Tests/BrainUsageSignalTests.cs", "ReinforceIgnoresNonNodeKeys"),
        ["learn: usage-blended recall ranks the reinforced node above an equal-relevance one"] =
            ("tests/Aitm.Brain.Tests/BrainUsageSignalTests.cs", "UsageBlendedRecallRanksTheReinforcedNodeAboveAnEqualRelevanceOne"),
        ["learn: re-asserting a triple strengthens conf without duplicating"] =
            ("tests/Aitm.Brain.Tests/BrainLearnToolTests.cs", "CliReassertingATripleStrengthensConfidenceWithoutDuplicating"),
        ["recall: hyphenated alias expansion is quoted (no FTS5 syntax crash)"] =
            ("tests/Aitm.Brain.Tests/BrainRecallToolTests.cs", "HyphenatedAliasCanonicalExpandsWithoutAnFts5SyntaxCrash"),
        ["recall: a synonym alias expands the query (hub -> signalr)"] =
            ("tests/Aitm.Brain.Tests/BrainRecallToolTests.cs", "ASynonymAliasExpandsTheQuery"),
        ["recall: substring fallback catches what FTS misses (dataresponse)"] =
            ("tests/Aitm.Brain.Tests/BrainRecallToolTests.cs", "CliShapeFallsBackToSubstringAndLogsAGapWhenNothingMatches"),
        ["merge: source retired, edge re-pointed, no dead reference"] =
            ("tests/Aitm.Brain.Tests/BrainMergeToolTests.cs", "MatchesTodaysCliOutputAndRepointsTheEdgeWithNoDeadReference"),
        ["forget: node + its edges retired (no orphaned reference)"] =
            ("tests/Aitm.Brain.Tests/BrainForgetToolTests.cs", "MatchesTodaysCliOutputAndRetiresTheNodeAndItsLiveEdges"),
        ["conflicts: requires+forbids on the same pair is detected"] =
            ("tests/Aitm.Brain.Tests/BrainGraphInvariantTests.cs", "RequiresAndForbidsOnTheSamePairIsDetectedAsAConflict"),
        ["verify: confirmation timestamp recorded"] =
            ("tests/Aitm.Brain.Tests/BrainVerifyToolTests.cs", "VerifyingALiveNodeRecordsAConfirmationTimestamp"),
        ["graph-query: finds the fixture symbol"] =
            ("tests/Aitm.Graph.Tests/GraphQueryToolTests.cs", "CliShapeMatchesTodaysCliOutputForAMatchingQuestion"),
        ["graph-query: shows its declaration site"] =
            ("tests/Aitm.Graph.Tests/GraphQueryToolTests.cs", "CliShapeMatchesTodaysCliOutputForAMatchingQuestion"),
        ["graph-query: groups the match under its home project"] =
            ("tests/Aitm.Graph.Tests/GraphQueryToolTests.cs", "CliShapeMatchesTodaysCliOutputForAMatchingQuestion"),
        ["graph-path: finds the 2-hop path"] =
            ("tests/Aitm.Graph.Tests/GraphPathToolTests.cs", "CliShapeFindsTheTwoHopPath"),
        ["graph-path: reports no path past depth 6"] =
            ("tests/Aitm.Graph.Tests/GraphPathToolTests.cs", "ReportsNoPathPastDepthSix"),
        ["graph-path: same node short-circuits"] =
            ("tests/Aitm.Graph.Tests/GraphPathToolTests.cs", "SameNodeShortCircuitsWithoutSearching"),
        ["graph-path: unresolvable name refuses cleanly"] =
            ("tests/Aitm.Graph.Tests/GraphPathToolTests.cs", "CliShapeRefusesCleanlyForAnUnresolvableSymbol"),
        ["graph-explain: reports the declaration kind and site"] =
            ("tests/Aitm.Graph.Tests/GraphExplainToolTests.cs", "CliShapeMatchesTodaysCliOutputForARecordedSymbol"),
        ["graph-explain: lists users grouped by project"] =
            ("tests/Aitm.Graph.Tests/GraphExplainToolTests.cs", "McpShapeMatchesTodaysMcpOutputForARecordedSymbol"),
    };

    [Fact]
    public void TheNameTableHasExactlySixtyThreeUniqueEntries()
    {
        Assert.Equal(63, SelfTestChecks.Length);
        Assert.Equal(63, SelfTestChecks.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheMappingCoversEveryNameInTheTable()
    {
        foreach (string check in SelfTestChecks)
            Assert.True(Coverage.ContainsKey(check), $"no mapped test for selftest check: {check}");
    }

    [Fact]
    public void AllSixtyThreeSelfTestChecksHaveAnEquivalentTest()
    {
        foreach (string check in SelfTestChecks)
        {
            (string file, string method) = Coverage[check];
            string fullPath = Path.Combine(RepoPaths.Root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"mapped file missing for check '{check}': {file}");

            string source = File.ReadAllText(fullPath);
            Regex signature = new($@"public\s+(?:async\s+)?void\s+{Regex.Escape(method)}\s*\(");
            Assert.True(signature.IsMatch(source),
                $"mapped method missing for check '{check}': {file} :: {method}");
        }
    }

    [Fact]
    public void SelftestVerbIsGoneFromAitmCs()
    {
        string source = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "Data", "CliDispatch.cs"));
        Assert.DoesNotMatch(SelfTestFunction(), source);
        Assert.Matches(SelfTestCase(), source); // still a case, but as a removed-verb message
        Assert.Contains("removed in 0.4: aitm selftest is gone", source);
    }

    [GeneratedRegex(@"void\s+SelfTest\s*\(")]
    private static partial Regex SelfTestFunction();
    [GeneratedRegex("case \"selftest\":")]
    private static partial Regex SelfTestCase();
}
