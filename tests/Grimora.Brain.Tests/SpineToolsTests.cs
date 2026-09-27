using Grimora.Brain.Data;
using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class SpineToolsTests
{
    // RESTRUCTURE.md slice 19: "Tests first: spine round trip (export, import into a temp store,
    // export again, same file)." Both tools are copied verbatim from grimora.cs's SpineExport/SpineImport
    // (grimora.cs:2174, grimora.cs:2230); neither has a test today, so this is pinned against the behaviour
    // rather than against the old binary — the round trip itself is the oracle.
    [Fact]
    public void ExportImportExportProducesTheSameSpineFile()
    {
        string sourceInstance = GrimoraCliRunner.NewTestInstance("spine-roundtrip-source");
        string targetInstance = GrimoraCliRunner.NewTestInstance("spine-roundtrip-target");
        string firstExport = Path.Combine(Path.GetTempPath(), $"spine-{Guid.NewGuid():N}-1.json");
        string secondExport = Path.Combine(Path.GetTempPath(), $"spine-{Guid.NewGuid():N}-2.json");
        try
        {
            GrimoraCliRunner.Run($"init --instance {sourceInstance}");
            using (SqliteConnection source = StoreConnection.Open(GrimoraCliRunner.InstanceDbPath(sourceInstance)))
            {
                BrainWriters.AddNode(source, "proj:widget", "project", "Widget", "the widget project", "", true, "fixture");
                BrainWriters.AddNode(source, "proj:gadget", "project", "Gadget", "the gadget project", "", false, "fixture");
                BrainWriters.AddTriple(source, "proj:widget", "related", "proj:gadget", "shares a seam", "seed", false, "fixture");
                BrainWriters.AddSlot(source, "proj:widget", "place", "src/widget", "text", false, "", "seed", "fixture");
                Exec(source, "INSERT INTO proj_alias(short,k) VALUES('wid','proj:widget')");
                Exec(source, "INSERT INTO term_alias(term,canonical) VALUES('widgets','widget')");
                Exec(source, "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES('Frobnicate','IWidget','proj:widget','src/Widget.cs',12,'call',0)");

                // term_alias ships pre-seeded with 11 default rows (BrainSchema.cs); this fixture adds one more.
                string exportMessage = new SpineExportTool().ExecuteCli(source, firstExport);
                Assert.Contains("2 nodes", exportMessage);
                Assert.Contains("1 slots", exportMessage);
                Assert.Contains("1 links", exportMessage);
                Assert.Contains("1 aliases", exportMessage);
                Assert.Contains("12 terms", exportMessage);
                Assert.Contains("1 edges", exportMessage);
            }
            Assert.True(File.Exists(firstExport));

            GrimoraCliRunner.Run($"init --instance {targetInstance}");
            using (SqliteConnection target = StoreConnection.Open(GrimoraCliRunner.InstanceDbPath(targetInstance)))
            {
                string importMessage = new SpineImportTool().ExecuteCli(target, firstExport);
                Assert.Equal("imported spine: 2 nodes, 1 slots, 1 links, 1 alias(es), 1 edge(s).", importMessage);
                // term_alias inserts use OR IGNORE, so the target's own 10 seeded rows plus the 1 imported
                // one land the same way the source's did — the second export below is what actually proves it.

                new SpineExportTool().ExecuteCli(target, secondExport);
            }

            Assert.Equal(File.ReadAllText(firstExport), File.ReadAllText(secondExport));
        }
        finally
        {
            if (File.Exists(firstExport)) File.Delete(firstExport);
            if (File.Exists(secondExport)) File.Delete(secondExport);
            GrimoraCliRunner.DeleteInstance(sourceInstance);
            GrimoraCliRunner.DeleteInstance(targetInstance);
        }
    }

    [Fact]
    public void ImportSkipsALinkWhoseEndpointIsMissingAndReportsItsCount()
    {
        string instance = GrimoraCliRunner.NewTestInstance("spine-import-missing-endpoint");
        string spineFile = Path.Combine(Path.GetTempPath(), $"spine-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(spineFile, """
                {
                  "nodes": [ { "k": "proj:widget", "kind": "project", "label": "Widget", "gloss": "", "scheme": "", "hard": 0 } ],
                  "slots": [],
                  "links": [ { "s": "proj:widget", "p": "related", "o": "proj:nowhere", "because": "" } ],
                  "aliases": [],
                  "terms": [],
                  "edges": []
                }
                """);
            GrimoraCliRunner.Run($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoraCliRunner.InstanceDbPath(instance));

            string message = new SpineImportTool().ExecuteCli(connection, spineFile);

            Assert.Equal("imported spine: 1 nodes, 0 slots, 0 links, 0 alias(es), 0 edge(s).", message);
        }
        finally
        {
            if (File.Exists(spineFile)) File.Delete(spineFile);
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void ImportReportsAMissingFileRatherThanThrowing()
    {
        string instance = GrimoraCliRunner.NewTestInstance("spine-import-missing-file");
        try
        {
            GrimoraCliRunner.Run($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoraCliRunner.InstanceDbPath(instance));
            string missing = Path.Combine(Path.GetTempPath(), $"no-such-spine-{Guid.NewGuid():N}.json");

            string message = new SpineImportTool().ExecuteCli(connection, missing);

            Assert.Equal($"no spine file at {missing}", message);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
