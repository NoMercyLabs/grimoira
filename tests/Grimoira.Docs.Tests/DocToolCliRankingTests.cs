using Grimoira.Docs.Schema;
using Grimoira.Docs.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Docs.Tests;

// The CLI doc search once printed up to 2 synthesis rows first (each with a 20,000-char snippet) and only then
// the best direct sections: on a real store the one section holding every query word came 62 lines down,
// behind an unrelated synthesis. ExecuteCli now ranks both kinds together by bm25, best first.
public class DocToolCliRankingTests
{
    [Fact]
    public void CliRanksTheSectionMatchingEveryTermAboveASynthesisMatchingOne()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("doc-cli-rank");
        try
        {
            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            Directory.CreateDirectory(GrimoiraCliRunner.InstanceDir(instance));
            using SqliteConnection connection = StoreConnection.Open(dbPath);
            foreach (string statement in new DocsSchema().Statements)
            {
                using SqliteCommand create = connection.CreateCommand();
                create.CommandText = statement;
                create.ExecuteNonQuery();
            }

            string synthesisBody = "zebrafinch overview. " + string.Concat(Enumerable.Repeat("filler words about nothing in particular. ", 120));
            Insert(connection, "syn-1", "C:/x/plans", "plans brief", "synthesis", synthesisBody);
            Insert(connection, "sec-1", "C:/x/plans/handover.md", "handover check", "doc",
                "zebrafinch handover check: the handover check for the zebrafinch lane runs before any push.");

            string output = new DocTool().ExecuteCli(connection, "zebrafinch handover check");

            string[] rows = output.Split("• [", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, rows.Length);
            Assert.StartsWith("doc] handover check", rows[0]);
            Assert.StartsWith("synthesis] plans brief", rows[1]);

            string synthesisSnippet = rows[1].Split('\n')[1].Trim();
            Assert.True(synthesisSnippet.Length <= 1501, $"synthesis snippet is {synthesisSnippet.Length} chars; the cap is 1,500 plus the ellipsis");
            Assert.EndsWith("…", synthesisSnippet);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static void Insert(SqliteConnection connection, string key, string path, string title, string category, string content)
    {
        using (SqliteCommand docs = connection.CreateCommand())
        {
            docs.CommandText = "INSERT INTO docs(k,path,title,category,content,terms) VALUES($k,$p,$t,$c,$co,'')";
            docs.Parameters.AddWithValue("$k", key);
            docs.Parameters.AddWithValue("$p", path);
            docs.Parameters.AddWithValue("$t", title);
            docs.Parameters.AddWithValue("$c", category);
            docs.Parameters.AddWithValue("$co", content);
            docs.ExecuteNonQuery();
        }
        using SqliteCommand fts = connection.CreateCommand();
        fts.CommandText = "INSERT INTO docs_fts(k,title,content) VALUES($k,$t,$co)";
        fts.Parameters.AddWithValue("$k", key);
        fts.Parameters.AddWithValue("$t", title);
        fts.Parameters.AddWithValue("$co", content);
        fts.ExecuteNonQuery();
    }
}
