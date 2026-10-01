using Grimoira.Brain.Tools;
using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

/// <summary>
/// Ports <c>ranking-agreement.test.mjs</c>: that test cross-checked the CLI's FTS5+bm25 ranker against
/// the hooks' LIKE+JS ranker, because node's bundled SQLite has no FTS5. With the CLI ranker moving to
/// C# and one ranker left (RESTRUCTURE.md slice 19), there is nothing left to cross-check against — so
/// this becomes the retrieval eval test instead: it seeds a fixture the same shape as the JS test's
/// (a handful of verified facts, one per query), runs <see cref="EvalTool"/>'s own built-in question set
/// against it, and pins the resulting scores as the baseline for the "smarter" goal. The original
/// <c>ranking-agreement.test.mjs</c> is left in place (it still exercises brain-lib.mjs's own ranker).
/// </summary>
public class RetrievalEvalTests
{
    [Fact]
    public void PinsTheEvalAccuracyOnAFixtureTunedToEveryBuiltInQuestion()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("retrieval-eval");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            AddTool add = new();

            // One fact per answerable question in EvalTool's built-in set (grimoira.cs:2375's Eval, copied
            // verbatim), each phrased so its terms cover the query and its value contains the expected
            // substring. Below the FloorMinCorpus gate (20 facts), so this exercises the coverage gate
            // rather than the relevance floor — the same regime the pre-existing SpineToolsTests-style
            // fixtures run under.
            add.Execute(connection, "media base url", "[]", "docs",
                "The media base URL is https://raw.githubusercontent.com/nomercy/assets", "fixture", "", "stated");
            add.Execute(connection, "unauthorized response code", "[]", "auth",
                "Unauthorized requests get response 403, not 401.", "fixture", "", "stated");
            add.Execute(connection, "database contexts count", "[]", "config",
                "There are THREE database contexts in this project.", "fixture", "", "stated");
            add.Execute(connection, "tv shows table name", "[]", "schema",
                "The tv shows table is called Tvs.", "fixture", "", "stated");
            add.Execute(connection, "default server port", "[]", "config", "7626", "fixture", "", "stated");
            add.Execute(connection, "storage driver types", "[]", "storage",
                "Supported storage drivers are local and nfs; smb is not supported.", "fixture", "", "stated");
            add.Execute(connection, "video item watch progress field", "[]", "schema",
                "The video item watch progress field is named timestamp.", "fixture", "", "stated");
            add.Execute(connection, "signalr hub routes", "[]", "api",
                "SignalR hub routes include videoHub for video playback events.", "fixture", "", "stated");
            add.Execute(connection, "encoder preset profile field", "[]", "schema",
                "The encoder preset profile field is stored as profile_json.", "fixture", "", "stated");
            add.Execute(connection, "windows installer release asset", "[]", "release",
                "The windows installer release asset is named ExampleInstaller.", "fixture", "", "stated");

            string result = new EvalTool().ExecuteCli(connection);

            // Baseline pinned empirically against the pre-move CLI oracle (grimoira.cs's Eval/Search):
            // 13 of 14 questions pass. The lone miss is "server" alone — a single generic token that, in
            // this small (sub-gate) fixture, still matches the "default server port" fact because the
            // relevance floor only engages once the corpus reaches FloorMinCorpus (20) facts. That is the
            // real ranker's own documented behaviour (grimoira.cs:58-64), not a defect in this port.
            Assert.Contains("accuracy 13/14 (93%)", result);
            Assert.Contains("\"media base url\"", result);
            Assert.Contains("raw.githubusercontent.com", result);
            Assert.Contains("FAIL", result);
            Assert.Contains("\"server\"", result);
            Assert.Contains("LEAKED: 7626", result);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
