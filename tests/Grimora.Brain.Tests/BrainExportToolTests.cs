using Grimora.Brain.Data;
using Grimora.Brain.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Brain.Tests;

public class BrainExportToolTests
{
    [Fact]
    public void CliShapeMatchesTodaysCliOutputAndWritesTheSameFile()
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("brain-export-old");
        string newInstance = GrimoraCliRunner.NewTestInstance("brain-export-new");
        string oldFile = Path.Combine(Path.GetTempPath(), $"brain-export-{Guid.NewGuid():N}-old.txt");
        string newFile = Path.Combine(Path.GetTempPath(), $"brain-export-{Guid.NewGuid():N}-new.txt");
        try
        {
            // Oracle: today's grimora.cs BrainExport() (grimora.cs:1995).
            string oldDb = GrimoraCliRunner.InstanceDbPath(oldInstance);
            GrimoraCliRunner.Run($"init --instance {oldInstance}");
            SeedGraph(oldDb);
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"brain export --instance {oldInstance} --to {oldFile}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim().Replace(oldFile, "<FILE>");

            string newDb = GrimoraCliRunner.InstanceDbPath(newInstance);
            GrimoraCliRunner.Run($"init --instance {newInstance}");
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
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
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
