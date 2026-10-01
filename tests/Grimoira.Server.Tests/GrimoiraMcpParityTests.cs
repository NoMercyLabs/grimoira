using Grimoira.Server.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Grimoira.Server.Tests;

/// <summary>
/// RESTRUCTURE.md Slice P1: "the 24 tools over `grimoira mcp` equal the HTTP snapshot parity cases (reuse
/// HttpSnapshotParityTests' cases)". Each case seeds the pinned pre-slice-24 mcp.dll snapshot's store and a
/// real service's store identically, then compares one normal and one error/miss call: the snapshot over
/// stdio against `grimoira mcp` (stdio) forwarding over the pipe to the real service.
/// </summary>
public sealed class GrimoiraMcpParityTests
{
    private static readonly string RepoRoot = Grimoira.Layout.Tests.RepoPaths.Root;

    public static IEnumerable<object[]> ToolCases() => HttpSnapshotParityTests.ToolCases();

    [Theory]
    [MemberData(nameof(ToolCases))]
    public async Task GrimoiraMcpToolMatchesTheSnapshotMcpDll(
        string toolName, Action<SqliteConnection> seed, object normalArgs, object errorArgs)
    {
        string oldDll = McpSnapshotHarness.EnsureBuilt(RepoRoot);
        string oldInstance = GrimoiraCliRunner.NewTestInstance($"mcp-bridge-parity-{toolName}");
        string newDataDir = Directory.CreateTempSubdirectory("grimoira-mcp-bridge-parity-").FullName;
        const string newInstance = "new";
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            HttpSnapshotParityTests.Seed(GrimoiraCliRunner.InstanceDbPath(oldInstance), seed);
            SqliteConnection.ClearAllPools();

            using (ProjectStore bootstrap = new(newDataDir)) bootstrap.Acquire(newInstance);
            HttpSnapshotParityTests.Seed(Path.Combine(newDataDir, newInstance, "grimoira.db"), seed);
            SqliteConnection.ClearAllPools();

            if (toolName == "brain_flush")
            {
                HttpSnapshotParityTests.WaitForNonEmptyFile(Path.Combine(GrimoiraCliRunner.InstanceDir(oldInstance), "pending-learn.jsonl"));
                HttpSnapshotParityTests.WaitForNonEmptyFile(Path.Combine(newDataDir, newInstance, "pending-learn.jsonl"));
            }

            (_, IReadOnlyList<string> oldResults) =
                McpProcess.Run(oldDll, oldInstance, [(toolName, normalArgs), (toolName, errorArgs)]);

            using RunningServer server = RunningServer.Start(newDataDir);
            await using McpClient client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "grimoira-parity",
                Command = "dotnet",
                Arguments = [RunningServer.CliDll, "mcp"],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["GRIMOIRA_DATA_DIR"] = newDataDir,
                    ["GRIMOIRA_INSTANCE"] = newInstance,
                },
            }));

            string normalText = await CallText(client, toolName, normalArgs);
            string errorText = await CallText(client, toolName, errorArgs);

            // Both sides are driven one call at a time (McpProcess.Run's default, McpClient's awaits), so
            // brain_flush is pinned exactly and compared like every other tool.
            if (toolName == "brain_flush")
            {
                Assert.Equal("flushed 1 learning(s) into the brain.", HttpSnapshotParityTests.StripTimestamps(oldResults[0]));
                Assert.Equal("nothing staged.", HttpSnapshotParityTests.StripTimestamps(oldResults[1]));
            }
            Assert.Equal(HttpSnapshotParityTests.StripTimestamps(oldResults[0]), HttpSnapshotParityTests.StripTimestamps(normalText));
            Assert.Equal(HttpSnapshotParityTests.StripTimestamps(oldResults[1]), HttpSnapshotParityTests.StripTimestamps(errorText));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(newDataDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    private static async Task<string> CallText(McpClient client, string toolName, object args)
    {
        Dictionary<string, object?> arguments = args.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(args));
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        return result.Content.Count > 0 && result.Content[0] is TextContentBlock text ? text.Text : "";
    }
}
