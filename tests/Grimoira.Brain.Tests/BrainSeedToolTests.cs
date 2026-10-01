using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

public class BrainSeedToolTests
{
    // RESTRUCTURE.md slice 19: `brain seed` is only a call to spine-import (moved in slice 17, oracle
    // now grimoira.cs's BrainSeed, grimoira.cs:2156). Pinned against the CLI oracle with an explicit --from so
    // neither side takes the AppContext.BaseDirectory-relative default path (the two processes' base
    // directories differ, so that branch is not comparable byte for byte across the two runs).
    [Fact]
    public void CliShapeMatchesTodaysCliOutputAndImportsTheSpine()
    {
        string oldInstance = GrimoiraCliRunner.NewTestInstance("brain-seed-old");
        string newInstance = GrimoiraCliRunner.NewTestInstance("brain-seed-new");
        string spineFile = Path.Combine(Path.GetTempPath(), $"brain-seed-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(spineFile, """
                {
                  "nodes": [ { "k": "proj:widget", "kind": "project", "label": "Widget", "gloss": "", "scheme": "", "hard": 0 } ],
                  "slots": [],
                  "links": [],
                  "aliases": [],
                  "terms": [],
                  "edges": []
                }
                """);

            // Oracle: today's grimoira.cs BrainSeed() (grimoira.cs:2156), delegating to SpineImport (grimoira.cs:2230).
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"brain seed --instance {oldInstance} --from {spineFile}");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            string newDb = GrimoiraCliRunner.InstanceDbPath(newInstance);
            GrimoiraCliRunner.Run($"init --instance {newInstance}");
            using SqliteConnection connection = StoreConnection.Open(newDb);
            string actual = new BrainSeedTool().ExecuteCli(connection, spineFile).Trim();

            Assert.Equal(expected, actual);
            Assert.Equal("imported spine: 1 nodes, 0 slots, 0 links, 0 alias(es), 0 edge(s).", actual);

            using SqliteCommand nodeCount = connection.CreateCommand();
            nodeCount.CommandText = "SELECT count(*) FROM node_now WHERE k='proj:widget'";
            Assert.Equal(1L, (long)nodeCount.ExecuteScalar()!);
        }
        finally
        {
            if (File.Exists(spineFile)) File.Delete(spineFile);
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            GrimoiraCliRunner.DeleteInstance(newInstance);
        }
    }

    // grimoira.cs's BrainSeed prints its "no spine file" hint to stderr (Console.Error.WriteLine), so it is
    // not present in GrimoiraCliRunner's captured stdout and cannot be diffed against the CLI oracle the way
    // the happy path above is. Pinned against the behaviour itself instead, the same way SpineToolsTests
    // pins SpineImportTool's own missing-file message.
    [Fact]
    public void ReportsAMissingSpineFileWithTheOnboardingHintRatherThanImporting()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("brain-seed-missing");
        string missing = Path.Combine(Path.GetTempPath(), $"no-such-spine-{Guid.NewGuid():N}.json");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            string message = new BrainSeedTool().ExecuteCli(connection, missing);

            Assert.Equal(
                $"no spine file at {Path.GetFullPath(missing)} — run `grimoira spine-export` on an instance that already has one, or write the file by hand (see README).",
                message);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
