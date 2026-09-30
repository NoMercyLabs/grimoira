using Grimora.Brain.Data;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

/// <summary>
/// Port of brain-lib.test.mjs (RESTRUCTURE.md slice 19, part 3) onto <see cref="BrainSearchLib"/>. Each
/// case is a regression for a bug that shipped in the JS scoring engine; the store here is a throwaway
/// instance built through <c>grimora init</c>, never the live one, same discipline as the .mjs original.
/// </summary>
public class BrainSearchLibTests
{
    private static SqliteConnection NewStore(string label, out string instance)
    {
        instance = GrimoraCliRunner.NewTestInstance(label);
        GrimoraCliRunner.Run($"init --instance {instance}");
        return StoreConnection.Open(GrimoraCliRunner.InstanceDbPath(instance));
    }

    private static void InsertMemory(SqliteConnection c, string k, string hook, string body, bool hard = false)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO memory(k,type,title,hook,body,hard) VALUES($k,'reference',$t,$h,$b,$hard)";
        cmd.Parameters.AddWithValue("$k", k);
        cmd.Parameters.AddWithValue("$t", k);
        cmd.Parameters.AddWithValue("$h", hook);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.Parameters.AddWithValue("$hard", hard ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static void InsertEdge(SqliteConnection c, string symbol, string project, string file, int line, string usage)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,'decl',$p,$f,$l,$u,0)";
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$p", project);
        cmd.Parameters.AddWithValue("$f", file);
        cmd.Parameters.AddWithValue("$l", line);
        cmd.Parameters.AddWithValue("$u", usage);
        cmd.ExecuteNonQuery();
    }

    private static void InsertChat(SqliteConnection c, string k, string text)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO chat(k,session,ts,role,text) VALUES($k,'s','2026-01-01','user',$t)";
        cmd.Parameters.AddWithValue("$k", k);
        cmd.Parameters.AddWithValue("$t", text);
        cmd.ExecuteNonQuery();
    }

    // 1 & 2: unranked LIMIT truncation, and a long memory hook keeps its head — brain-lib.test.mjs:35-55.
    [Fact]
    public void ExactMatchSurvivesABroadTokenMatching120RowsAndKeepsItsLongHead()
    {
        SqliteConnection c = NewStore("search-broad-match", out string instance);
        try
        {
            for (int i = 0; i < 120; i++)
                InsertMemory(c, $"noise-{i}", $"something about the player number {i}", "filler body text");

            InsertMemory(c, "teleport",
                "Any app-web overlay meant to appear OVER the fullscreen video player must Teleport to body as well, or z-index cannot reach it",
                "the player is body-level at z-1199 so overlays must teleport too");

            List<string> tokens = BrainSearchLib.Tokenize("teleport overlay fullscreen player");
            List<BrainSearchLib.SearchHit> picks = BrainSearchLib.Search(c, tokens);

            Assert.True(picks.Count > 0);
            Assert.Contains("teleport", picks[0].Head, StringComparison.OrdinalIgnoreCase);
            Assert.True(picks[0].Head.Length > 90);
        }
        finally { c.Dispose(); GrimoraCliRunner.DeleteInstance(instance); }
    }

    // 3: conversation history can place in results — brain-lib.test.mjs:57-61.
    [Fact]
    public void ConversationHistoryCanPlaceInResults()
    {
        SqliteConnection c = NewStore("search-chat", out string instance);
        try
        {
            InsertChat(c, "c1", "we decided the encoder must stream copy when the codec already matches the target");
            List<BrainSearchLib.SearchHit> picks = BrainSearchLib.Search(c,
                BrainSearchLib.Tokenize("encoder stream copy codec matches target"));
            Assert.Contains(picks, p => p.KindTag == "chat");
        }
        finally { c.Dispose(); GrimoraCliRunner.DeleteInstance(instance); }
    }

    // 4: a code symbol must match on a word boundary, not a substring — brain-lib.test.mjs:63-72.
    [Fact]
    public void SubstringTokenDoesNotMatchASymbolButWholeSymbolDoes()
    {
        SqliteConnection c = NewStore("search-symbol-boundary", out string instance);
        try
        {
            InsertEdge(c, "aChromeCanTakeOverAndTheTrackerStandsDown", "p", "/x/a.kt", 1, "kotlin declaration");
            List<BrainSearchLib.SearchHit> standPicks = BrainSearchLib.Search(c, BrainSearchLib.Tokenize("what does grimora stand for"));
            Assert.DoesNotContain(standPicks, p => p.KindTag == "code");

            InsertEdge(c, "VideoPlaylistItem", "video-player", "/x/types.ts", 65, "ts declaration");
            List<BrainSearchLib.SearchHit> symPicks = BrainSearchLib.Search(c, BrainSearchLib.Tokenize("VideoPlaylistItem"));
            Assert.Contains(symPicks, p => p.KindTag == "code");
        }
        finally { c.Dispose(); GrimoraCliRunner.DeleteInstance(instance); }
    }

    // 5: track record cites the class name; a generic stem does not drag in everything — brain-lib.test.mjs:74-85.
    [Fact]
    public void HistoryMatchesADecisionCitingTheClassNameButIgnoresAGenericStem()
    {
        SqliteConnection c = NewStore("search-history", out string instance);
        try
        {
            InsertMemory(c, "smartcopy", "Encoder must stream-copy when the source matches the target",
                "wired via PlanStage.ApplySmartCopyDowngrade into StreamActionResolver");
            List<BrainSearchLib.SearchHit> hist = BrainSearchLib.HistoryFor(c, "c:/repo/src/Pipeline/Stages/PlanStage.cs");
            Assert.Contains(hist, h => (h.Head + h.Body).Contains("stream-copy", StringComparison.OrdinalIgnoreCase));

            InsertMemory(c, "desktopfiles", "Never write files to the user's Desktop", "unrelated to any view");
            List<BrainSearchLib.SearchHit> deskHist = BrainSearchLib.HistoryFor(c, "c:/repo/src/views/Watch/Desktop.vue");
            Assert.DoesNotContain(deskHist, h => h.Head.Contains("Never write files", StringComparison.OrdinalIgnoreCase));
        }
        finally { c.Dispose(); GrimoraCliRunner.DeleteInstance(instance); }
    }

    // 6: blast radius spots a symbol carried by more than one project — brain-lib.test.mjs:87-94.
    [Fact]
    public void BlastRadiusFlagsACrossProjectSymbolButNotASingleProjectOne()
    {
        SqliteConnection c = NewStore("search-blast", out string instance);
        try
        {
            InsertEdge(c, "AudioTrackState", "video-player", "/repo/video/types.ts", 10, "ts declaration");
            InsertEdge(c, "AudioTrackState", "music-player", "/repo/music/types.ts", 12, "ts declaration");
            InsertEdge(c, "LocalOnly", "video-player", "/repo/video/types.ts", 20, "ts declaration");

            List<BrainGraphLib.BlastRow> blast = BrainGraphLib.BlastRadius(c, "/repo/video/types.ts");
            Assert.Contains(blast, b => b is { Symbol: "AudioTrackState", Projects: > 1 });
            Assert.DoesNotContain(blast, b => b.Symbol == "LocalOnly");
        }
        finally { c.Dispose(); GrimoraCliRunner.DeleteInstance(instance); }
    }

    // 7: signature is order independent — brain-lib.test.mjs:96-97.
    [Fact]
    public void SignatureIsOrderIndependent()
    {
        Assert.Equal(
            BrainSearchLib.Signature(BrainSearchLib.Tokenize("alpha beta")),
            BrainSearchLib.Signature(BrainSearchLib.Tokenize("beta alpha")));
    }
}
