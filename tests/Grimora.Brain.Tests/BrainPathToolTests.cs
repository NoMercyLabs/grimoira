using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

/// <summary>
/// Cases pinned from reading brain-path.mjs directly (RESTRUCTURE.md slice 19, part 3): the script has
/// no test today and no grimora.cs/mcp.cs oracle to run against a compiled binary, so each expected string
/// below is derived from the script's own formatting (brain-path.mjs:122-148, :21-35, :95-120).
/// </summary>
public class BrainPathToolTests
{
    private static SqliteConnection NewStore(string label, out string instance)
    {
        instance = GrimoraCliRunner.NewTestInstance(label);
        GrimoraCliRunner.Run($"init --instance {instance}");
        return StoreConnection.Open(GrimoraCliRunner.InstanceDbPath(instance));
    }

    private static void InsertNode(SqliteConnection c, string k, string kind, string label, string scheme = "", string gloss = "")
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO node(k,kind,label,gloss,scheme) VALUES($k,$kind,$label,$gloss,$scheme)";
        cmd.Parameters.AddWithValue("$k", k);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$label", label);
        cmd.Parameters.AddWithValue("$gloss", gloss);
        cmd.Parameters.AddWithValue("$scheme", scheme);
        cmd.ExecuteNonQuery();
    }

    private static void InsertTriple(SqliteConnection c, string s, string p, string o, string because = "")
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO triple(s,p,o,o_is_literal,because) VALUES($s,$p,$o,0,$because)";
        cmd.Parameters.AddWithValue("$s", s);
        cmd.Parameters.AddWithValue("$p", p);
        cmd.Parameters.AddWithValue("$o", o);
        cmd.Parameters.AddWithValue("$because", because);
        cmd.ExecuteNonQuery();
    }

    // brain-path.mjs:133-148: a two-hop chain, formatted "from -> to (N hop(s))" then one line per hop.
    [Fact]
    public void PathBetweenReportsTheShortestChainWithReasons()
    {
        SqliteConnection c = NewStore("path-chain", out string instance);
        try
        {
            InsertNode(c, "proj:web", "project", "nomercy-app-web");
            InsertNode(c, "proj:hub", "project", "NoMercy Connect hub");
            InsertNode(c, "proj:server", "project", "nomercy-media-server");
            InsertTriple(c, "proj:web", "consumes", "proj:hub", "shares the connect protocol");
            InsertTriple(c, "proj:hub", "consumes", "proj:server", "hub calls the media server");

            string actual = new BrainPathTool().ExecuteCli(c, ["nomercy-app-web", "nomercy-media-server"]);

            Assert.Equal(
                "nomercy-app-web  ->  nomercy-media-server   (2 hop(s))\n" +
                "  -> consumes  proj:hub   (shares the connect protocol)\n" +
                "  -> consumes  proj:server   (hub calls the media server)",
                actual);
        }
        finally { c.Dispose(); GrimoraCliRunner.DeleteInstance(instance); }
    }

    // brain-path.mjs:136-140.
    [Fact]
    public void PathBetweenReportsAnUnknownNodeOrNoConnection()
    {
        SqliteConnection c = NewStore("path-unknown", out string instance);
        try
        {
            InsertNode(c, "proj:web", "project", "nomercy-app-web");
            InsertNode(c, "proj:isolated", "project", "isolated-repo");

            Assert.Equal("no node matches \"nothing-like-this\"",
                new BrainPathTool().ExecuteCli(c, ["nothing-like-this", "nomercy-app-web"]));

            Assert.Equal(
                "no recorded connection between \"nomercy-app-web\" and \"isolated-repo\".",
                new BrainPathTool().ExecuteCli(c, ["nomercy-app-web", "isolated-repo"]));
        }
        finally { c.Dispose(); GrimoraCliRunner.DeleteInstance(instance); }
    }

    // brain-path.mjs:21-34.
    [Fact]
    public void BlastReportsSharedSymbolsOrTheNoSharingMessage()
    {
        SqliteConnection c = NewStore("path-blast", out string instance);
        try
        {
            using (SqliteCommand cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES" +
                    "('Shared','decl','video-player','/repo/video/types.ts',1,'',0)," +
                    "('Shared','decl','music-player','/repo/music/types.ts',1,'',0)";
                cmd.ExecuteNonQuery();
            }

            string actual = new BrainPathTool().ExecuteCli(c, ["--blast", "/repo/video/types.ts"]);
            Assert.StartsWith("blast radius for /repo/video/types.ts:", actual);
            Assert.Contains("Shared", actual);

            string none = new BrainPathTool().ExecuteCli(c, ["--blast", "/repo/nothing.ts"]);
            Assert.Equal(
                "no shared symbols found for /repo/nothing.ts — nothing else in the graph carries its declarations.",
                none);
        }
        finally { c.Dispose(); GrimoraCliRunner.DeleteInstance(instance); }
    }

    // brain-path.mjs:95-107.
    [Fact]
    public void ClustersReportsNoClustersWhenTheGraphIsEmpty()
    {
        SqliteConnection c = NewStore("path-clusters-empty", out string instance);
        try
        {
            Assert.Equal("no clusters — the triple graph has no connected nodes yet.",
                new BrainPathTool().ExecuteCli(c, ["--clusters"]));
        }
        finally { c.Dispose(); GrimoraCliRunner.DeleteInstance(instance); }
    }

    // brain-path.mjs:109-120.
    [Fact]
    public void ClusterReportsAnUnconnectedNode()
    {
        SqliteConnection c = NewStore("path-cluster-lonely", out string instance);
        try
        {
            InsertNode(c, "proj:lonely", "project", "lonely-repo");
            Assert.Equal("\"lonely-repo\" is not connected to anything else yet.",
                new BrainPathTool().ExecuteCli(c, ["--cluster", "lonely-repo"]));
        }
        finally { c.Dispose(); GrimoraCliRunner.DeleteInstance(instance); }
    }
}
