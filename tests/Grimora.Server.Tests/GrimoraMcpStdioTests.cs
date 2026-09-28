using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Grimora.Facts.Tools;
using Grimora.Layout.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Grimora.Server.Tests;

// RESTRUCTURE.md Slice P1: "`grimora mcp` is Claude Code's MCP server over stdio". The published CLI runs as its
// own process against a real service on a temp data dir (so a pipe name of its own). stdout is the protocol
// stream: only JSON-RPC frames may appear on it.
public sealed class GrimoraMcpStdioTests : IDisposable
{
    private const string Instance = "mcp-stdio";
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimora-mcp-stdio-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private Task<McpClient> ConnectAsync(IDictionary<string, string?>? extraEnv = null)
    {
        Dictionary<string, string?> env = new()
        {
            ["GRIMORA_DATA_DIR"] = _dataDir,
            ["GRIMORA_INSTANCE"] = Instance,
        };
        if (extraEnv is not null) foreach ((string k, string? v) in extraEnv) env[k] = v;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "grimora-under-test",
            Command = "dotnet",
            Arguments = [RunningServer.CliDll, "mcp"],
            EnvironmentVariables = env,
        }));
    }

    private void SeedFact()
    {
        RunningServer.SeedInstance(_dataDir, Instance, c => new AddTool().Execute(c, "stdio-term", "", "manual", "stdio-answer", "src", "", "stated"));
    }

    [Fact]
    public async Task InitializeListsTheGoldenToolsAndCallsGoThroughTheService()
    {
        SeedFact();
        using RunningServer server = RunningServer.Start(_dataDir);
        await using McpClient client = await ConnectAsync();

        IList<McpClientTool> tools = await client.ListToolsAsync();
        CallToolResult result = await client.CallToolAsync("history", new Dictionary<string, object?> { ["term"] = "stdio-term" });

        Assert.Equal(GoldenListsTests.GoldenMcpTools.Append("chat_list").Append("chat_count").OrderBy(n => n), tools.Select(t => t.Name).OrderBy(n => n));
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("stdio-term", ((TextContentBlock)result.Content[0]).Text);
        Assert.All(tools, t => Assert.Equal(JsonValueKind.Object, t.JsonSchema.ValueKind));
    }

    [Fact]
    public async Task AToolErrorComesBackAsAnMcpToolErrorAndTheSessionGoesOn()
    {
        SeedFact();
        using RunningServer server = RunningServer.Start(_dataDir);
        await using McpClient client = await ConnectAsync();

        CallToolResult bad = await client.CallToolAsync("history", new Dictionary<string, object?> { ["term"] = new[] { 1, 2 } });
        CallToolResult good = await client.CallToolAsync("history", new Dictionary<string, object?> { ["term"] = "stdio-term" });

        Assert.True(bad.IsError);
        Assert.NotEqual(true, good.IsError);
    }

    [Fact]
    public async Task StdoutCarriesOnlyJsonRpcFrames()
    {
        SeedFact();
        using RunningServer server = RunningServer.Start(_dataDir);
        ProcessStartInfo psi = new("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(RunningServer.CliDll);
        psi.ArgumentList.Add("mcp");
        psi.Environment["GRIMORA_DATA_DIR"] = _dataDir;
        psi.Environment["GRIMORA_INSTANCE"] = Instance;
        using Process cli = Process.Start(psi)!;
        Task<string> stderr = cli.StandardError.ReadToEndAsync();

        await cli.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}");
        await cli.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await cli.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        await cli.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"history\",\"arguments\":{\"term\":[1]}}}");
        await cli.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"no_such_tool\",\"arguments\":{}}}");
        await cli.StandardInput.FlushAsync();

        List<string> lines = [];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        while (lines.Count < 4)
        {
            string? line = await cli.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) break;
            lines.Add(line);
        }
        cli.StandardInput.Close();
        Assert.True(cli.WaitForExit(20000), "grimora mcp did not exit when stdin closed");
        string rest = await cli.StandardOutput.ReadToEndAsync();

        Assert.Equal(4, lines.Count);
        Assert.All(lines.Append(rest).Where(l => l.Length > 0), l =>
        {
            using JsonDocument frame = JsonDocument.Parse(l);
            Assert.Equal("2.0", frame.RootElement.GetProperty("jsonrpc").GetString());
        });
        _ = await stderr;
    }

    [Fact]
    public async Task WithNoServerAndNoneToStartItExitsOneWithNothingOnStdout()
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(RunningServer.CliDll);
        psi.ArgumentList.Add("mcp");
        psi.Environment["GRIMORA_DATA_DIR"] = _dataDir;
        psi.Environment["GRIMORA_SERVER_EXE"] = Path.Combine(_dataDir, "no-such-server.exe");
        using Process cli = Process.Start(psi)!;
        Task<string> stdout = cli.StandardOutput.ReadToEndAsync();
        Task<string> stderr = cli.StandardError.ReadToEndAsync();

        Assert.True(cli.WaitForExit(30000), "grimora mcp did not give up on a server it cannot start");

        Assert.Equal(1, cli.ExitCode);
        Assert.Equal("", await stdout);
        Assert.Contains("grimora mcp", await stderr);
    }
}
