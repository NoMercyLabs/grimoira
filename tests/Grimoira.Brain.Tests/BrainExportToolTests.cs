using Grimoira.Brain.Data;
using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainExportToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputAndWritesTheSameFile()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-export-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-export-new");
        string oldFile = Path.Combine(Path.GetTempPath(), $"brain-export-{Guid.NewGuid():N}-old.txt");
        string newFile = Path.Combine(Path.GetTempPath(), $"brain-export-{Guid.NewGuid():N}-new.txt");
        try
        {
            // Oracle: today's grimoira.cs BrainExport() (grimoira.cs:1995).
            string oldDb = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            SeedGraph(oldDb);
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain export --instance {oldInstance} --to {oldFile}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim().Replace(oldFile, "<FILE>");

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
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
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
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
