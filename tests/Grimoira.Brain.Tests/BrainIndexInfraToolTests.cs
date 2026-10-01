using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

/// <summary>
/// index-infra.mjs (RESTRUCTURE.md slice 19, part 3) read against a fixture file, never a real infra
/// description. Oracle is the script's own section-to-node-kind mapping (index-infra.mjs:35-53).
/// </summary>
public class BrainIndexInfraToolTests
{
    [Fact]
    public void ImportsHostsServicesAndEnvironmentsFromTheFixtureFile()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("index-infra");
        string tmp = Path.Combine(Path.GetTempPath(), $"grimoira-infra-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            string fromPath = Path.Combine(tmp, "infra.json");
            string outPath = Path.Combine(tmp, "infra-fragment.json");
            File.WriteAllText(fromPath, """
                {
                  "hosts": [{ "id": "lab-nas", "label": "Lab NAS", "detail": "Synology box on the LAN" }],
                  "services": [{ "id": "media-server", "label": "media server", "detail": "the .NET media server", "runsOn": "lab-nas", "why": "hosted there" }],
                  "environments": [{ "id": "prod", "label": "production", "detail": "the live environment" }]
                }
                """);

            string result = new BrainIndexInfraTool().ExecuteCli(connection, fromPath, outPath);

            Assert.StartsWith($"3 node(s), 1 link(s) from {fromPath}", result);
            Assert.Contains("imported spine: 3 nodes, 0 slots, 1 links, 0 alias(es), 0 edge(s).", result);

            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM node WHERE valid_to IS NULL AND k IN ('host:lab-nas','service:media-server','environment:prod')";
            Assert.Equal(3L, (long)cmd.ExecuteScalar()!);

            using SqliteCommand cmd2 = connection.CreateCommand();
            cmd2.CommandText = "SELECT count(*) FROM triple WHERE valid_to IS NULL AND s='service:media-server' AND p='belongs_in' AND o='host:lab-nas'";
            Assert.Equal(1L, (long)cmd2.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void ReportsAMissingInfraFile()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("index-infra-missing");
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));
            string missing = Path.Combine(Path.GetTempPath(), $"no-such-infra-{Guid.NewGuid():N}.json");

            string result = new BrainIndexInfraTool().ExecuteCli(connection, missing, missing + ".out");

            Assert.StartsWith($"no infra description at {missing}", result);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
