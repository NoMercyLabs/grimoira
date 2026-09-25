using Aitm.Brain.Data;
using Aitm.Brain.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Brain.Tests;

public class BrainExportToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputAndWritesTheSameFile()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("brain-export-old");
        string newInstance = AitmCliRunner.NewTestInstance("brain-export-new");
        string oldFile = Path.Combine(Path.GetTempPath(), $"brain-export-{Guid.NewGuid():N}-old.txt");
        string newFile = Path.Combine(Path.GetTempPath(), $"brain-export-{Guid.NewGuid():N}-new.txt");
        try
        {
            // Oracle: today's aitm.cs BrainExport() (aitm.cs:1995).
            string oldDb = AitmCliRunner.InstanceDbPath(oldInstance);
            AitmCliRunner.Run($"init --instance {oldInstance}");
            SeedGraph(oldDb);
            (string stdout, int exitCode) = AitmCliRunner.Run($"brain export --instance {oldInstance} --to {oldFile}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim().Replace(oldFile, "<FILE>");

            string newDb = AitmCliRunner.InstanceDbPath(newInstance);
            AitmCliRunner.Run($"init --instance {newInstance}");
            SeedGraph(newDb);
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainExportTool().ExecuteCli(connection, newFile).Trim().Replace(newFile, "<FILE>");

            Assert.Equal(expected, actual);
            Assert.Equal("exported 2 nodes / 1 triples / 1 slots -> <FILE>", actual);
            Assert.True(File.Exists(oldFile));
            Assert.Equal(File.ReadAllText(oldFile), File.ReadAllText(newFile));
        }
        finally
        {
            if (File.Exists(oldFile)) File.Delete(oldFile);
            if (File.Exists(newFile)) File.Delete(newFile);
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    private static void SeedGraph(string dbPath)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        BrainWriters.AddNode(connection, "proj:widget", "project", "Widget", "the widget project", "", true, "fixture");
        BrainWriters.AddNode(connection, "proj:gadget", "project", "Gadget", "the gadget project", "", false, "fixture");
        BrainWriters.AddTriple(connection, "proj:widget", "related", "proj:gadget", "shares a seam", "seed", false, "fixture");
        BrainWriters.AddSlot(connection, "proj:widget", "place", "src/widget", "text", false, "", "seed", "fixture");
    }
}
