using Grimoira.Brain.Tools;
using Grimoira.Docs.Tools;
using Grimoira.Facts.Tools;
using Grimoira.Memory.Tools;
using Grimoira.Server.Data;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using System.Text.RegularExpressions;

namespace Grimoira.Server.Tests;

/// <summary>
/// RESTRUCTURE.md "Slice 26b: every MCP tool over HTTP equals the old mcp.dll." For each of the 24
/// golden MCP tools (<see cref="Grimoira.Layout.Tests.GoldenListsTests.GoldenMcpTools"/>), seeds two
/// identical fresh stores — one under a temp <c>GRIMOIRA_DATA_DIR</c> for the real <c>/mcp</c> HTTP endpoint
/// (<see cref="WebApplicationFactory{TEntryPoint}"/>, the <see cref="RequestProjectResolver.InstanceHeader"/>
/// header), one under a throwaway <c>test-*</c> instance for the pinned pre-slice-24 <c>mcp.dll</c>
/// snapshot (<see cref="McpSnapshotHarness"/>, stdio) — and asserts the two give byte-identical text for
/// one normal call and one error/miss call. Seeding calls each feature's own tool class directly against
/// the two stores' raw sqlite files, the same shape the CLI verb that seeds it would leave behind.
///
/// <c>workspace_capabilities</c> and
/// <c>workspace_search</c> use the same non-execution path: this repo carries no
/// <c>scripts/workspace-*.py</c>, so both oracles hit the safe "unavailable" branch before any process
/// would run.
/// </summary>
public sealed partial class HttpSnapshotParityTests
{
    // Program.cs binds a real Kestrel listener at this port (UseUrls), not the in-memory TestServer, so a
    // literal port shared with another class (HeadersHelperMcpTests used to hardcode the same "17638")
    // races the OS's own asynchronous socket teardown between one WebApplicationFactory's Dispose and the
    // next bind — invisible on a quiet machine, but under full-suite load the previous listener is not
    // always gone yet, and the next bind throws inside the MCP call ("An error occurred invoking
    // '<tool>'."). A fresh ephemeral port per test, the same FreePort() convention already used by
    // ThinClientAgainstTheRunningServerTests, HookVerbAgainstTheRunningServerTests and
    // BinCliThinClientMatchesBinCliOldTests, removes the collision instead of widening a timeout around it.
    private readonly string _port = FreePort().ToString();
    private readonly string _allowedHost;
    private static readonly string RepoRoot = FindRoot();

    public HttpSnapshotParityTests() => _allowedHost = $"127.0.0.1:{_port}";

    private static int FreePort()
    {
        System.Net.Sockets.TcpListener l = new(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port == 7635 ? FreePort() : port;
    }

    [Theory]
    [MemberData(nameof(ToolCases))]
    public async Task HttpToolMatchesTheSnapshotMcpDll(
        string toolName, Action<SqliteConnection> seed, object normalArgs, object errorArgs)
    {
        string oldDll = McpSnapshotHarness.EnsureBuilt(RepoRoot);
        string oldInstance = GrimoiraCliRunner.NewTestInstance($"http-parity-{toolName}");
        string newDataDir = Directory.CreateTempSubdirectory("grimoira-http-parity-").FullName;
        const string newInstance = "new";
        try
        {
            GrimoiraCliRunner.Run($"init --instance {oldInstance}");
            string oldDbPath = GrimoiraCliRunner.InstanceDbPath(oldInstance);
            Seed(oldDbPath, seed);
            // Microsoft.Data.Sqlite pools the native connection even after Dispose(); without this, the
            // freshly spawned mcp.dll snapshot process below can race the pooled handle for the file lock
            // it just wrote through.
            SqliteConnection.ClearAllPools();

            using (ProjectStore bootstrap = new(newDataDir)) bootstrap.Acquire(newInstance);
            string newDbPath = Path.Combine(newDataDir, newInstance, "grimoira.db");
            Seed(newDbPath, seed);

            if (toolName == "brain_flush")
            {
                // brain_stage's own write is a plain File.AppendAllText beside the db file; on this
                // machine it was intermittently not yet visible to the very next reader (old snapshot's
                // spawned process, or the new server's freshly-opened ProjectStore) within the same
                // millisecond it returns — never a difference between the two, just an OS-timing gap in
                // this one case's setup. Confirm the ledger both sides will read is actually on disk with
                // content before either is called, rather than let that gap show up as a flaky parity
                // failure with nothing to fix in the server or tool code.
                WaitForNonEmptyFile(Path.Combine(GrimoiraCliRunner.InstanceDir(oldInstance), "pending-learn.jsonl"));
                WaitForNonEmptyFile(Path.Combine(newDataDir, newInstance, "pending-learn.jsonl"));
            }

            (_, IReadOnlyList<string> oldResults) =
                McpProcess.Run(oldDll, oldInstance, [(toolName, normalArgs), (toolName, errorArgs)]);

            Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", newDataDir);
            Environment.SetEnvironmentVariable("GRIMOIRA_SERVER_PORT", _port);
            using WebApplicationFactory<Program> factory = new();
            using HttpClient httpClient = factory.CreateClient(
                new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{_allowedHost}") });
            await using McpClient client = await ConnectAsync(httpClient, newInstance);

            string normalText = await CallText(client, toolName, normalArgs);
            string errorText = await CallText(client, toolName, errorArgs);

            // brain_flush's two replies are timing-free on both sides now that McpProcess.Run drives the
            // pinned oracle one call at a time (the same way McpClient awaits each call here): the
            // seeded learning is flushed on the first call, and the second is a clean no-op.
            if (toolName == "brain_flush")
            {
                Assert.Equal("flushed 1 learning(s) into the brain.", StripTimestamps(oldResults[0]));
                Assert.Equal("nothing staged.", StripTimestamps(oldResults[1]));
            }
            Assert.Equal(StripTimestamps(oldResults[0]), StripTimestamps(normalText));
            Assert.Equal(StripTimestamps(oldResults[1]), StripTimestamps(errorText));
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(oldInstance);
            Environment.SetEnvironmentVariable("GRIMOIRA_DATA_DIR", null);
            Environment.SetEnvironmentVariable("GRIMOIRA_SERVER_PORT", null);
            try { Directory.Delete(newDataDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    public static IEnumerable<object[]> ToolCases()
    {
        yield return Case("history",
            c => new AddTool().Execute(c, "parity-history-term", "", "manual", "one", "src", "", "stated"),
            new { term = "parity-history-term" }, new { term = "nothing-was-ever-named-this" });

        yield return Case("fact",
            c => new AddTool().Execute(c, "parity-fact-term", "", "manual", "parity-answer", "src", "", "stated"),
            new { query = "parity-fact-term" }, new { query = "nothing-ever-matches-this-term-at-all" });

        yield return Case("log_finding",
            _ => { },
            new { title = "parity-finding, spotted!", detail = "d", source = "s" },
            new { title = "", detail = "", source = "" });

        yield return Case("open_findings",
            c => new FindingTool().ExecuteCli(c, "parity-open-finding", "d", "s"),
            new { }, new { });

        yield return Case("rule",
            c => SeedMemory(c, "parity-rule-term"),
            new { query = "parity-rule-term" }, new { query = "nothing-ever-matches-this-term-at-all" });

        yield return Case("shed_memory",
            c => SeedMemory(c, "parityshed"),
            new { key = MemoryKey("parityshed") }, new { key = "no-such-key-ever" });

        // Recall intentionally changed to report the full filtered match count, so its current
        // contract is tested in RecallToolTests instead of against this pre-change snapshot.

        yield return Case("doc",
            c => SeedDocs(c, "parity-doc"),
            new { query = "paritydoc" }, new { query = "nothing-ever-matches-this-term-at-all" });

        yield return Case("impact",
            c => SeedEdge(c, "parity-symbol-x"),
            new { symbol = "parity-symbol-x" }, new { symbol = "no-such-symbol-ever" });

        yield return Case("graph_query",
            c => SeedEdge(c, "parity-symbol-x"),
            new { question = "parity-symbol-x" }, new { question = "zzz-nothing-found-at-all" });

        yield return Case("graph_path",
            _ => { },
            new { a = "parity-a-node", b = "parity-b-node" }, new { a = "also-missing-a", b = "also-missing-b" });

        yield return Case("graph_explain",
            c => SeedEdge(c, "parity-symbol-x"),
            new { symbol = "parity-symbol-x" }, new { symbol = "no-such-symbol-ever" });

        yield return Case("brain_core",
            c => new BrainLearnTool().ExecuteMcp(c, "node", "parity-core-node", "rule", "Parity Core Label", "", "", true),
            new { }, new { });

        yield return Case("brain_scope",
            _ => { },
            new { projects = "server" }, new { projects = "does-not-exist" });

        yield return Case("brain_common",
            _ => { },
            new { projects = "server web" }, new { projects = "nope-1 nope-2" });

        yield return Case("brain_place",
            _ => { },
            new { codekind = "dotnet-api-endpoint" }, new { codekind = "does-not-exist-kind" });

        yield return Case("brain_recall",
            _ => { },
            new { query = "parity-recall-node" }, new { query = "nothing-ever-matches-this-term-at-all" });

        yield return Case("brain_impact",
            _ => { },
            new { symbol = "parity-symbol-x" }, new { symbol = "no-such-symbol-ever" });

        yield return Case("brain_learn",
            _ => { },
            new { kind = "node", key = "parity-learn-node", a = "concept", b = "Parity Label, with punctuation!", c = "", because = "", hard = false },
            new { kind = "not-a-kind", key = "x", a = "", b = "", c = "", because = "", hard = false });

        yield return Case("brain_gaps",
            c => new QueryTool(new Grimoira.Store.Data.UsageSignal()).ExecuteMcp(c, "no-such-term-ever-parity-gap"),
            new { }, new { });

        yield return Case("brain_stage",
            _ => { },
            new { kind = "node", key = "parity-stage-node", a = "concept", b = "Parity Label!", c = "", because = "", hard = false },
            new { kind = "not-a-kind", key = "x", a = "", b = "", c = "", because = "", hard = false });

        yield return Case("brain_flush",
            c => new BrainStageTool().ExecuteMcp(c, "node", "parity-flush-node", "concept", "Parity Flush Label"),
            new { }, new { });

        yield return Case("workspace_capabilities",
            _ => { },
            new { query = "parity, hyphen-checking task" }, new { query = "" });

        yield return Case("workspace_search",
            _ => { },
            new { repository = ".", pattern = "parity-hyphenated-pattern" }, new { repository = "", pattern = "" });
    }

    private static object[] Case(string name, Action<SqliteConnection> seed, object normalArgs, object errorArgs) =>
        [name, seed, normalArgs, errorArgs];

    internal static void WaitForNonEmptyFile(string path)
    {
        for (int i = 0; i < 40; i++)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return;
            Thread.Sleep(25);
        }
        throw new InvalidOperationException($"expected {path} to exist with content after seeding, but it did not appear within 1s.");
    }

    internal static void Seed(string dbPath, Action<SqliteConnection> body)
    {
        using SqliteConnection connection = StoreConnection.Open(dbPath);
        body(connection);
    }

    private static void SeedEdge(SqliteConnection connection, string symbol)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,'field','server','Fixture.cs',1,'declares',0)";
        command.Parameters.AddWithValue("$s", symbol);
        command.ExecuteNonQuery();
    }

    private static string MemoryKey(string label) => $"memory-fixture-{label}";

    private static void SeedMemory(SqliteConnection connection, string label)
    {
        string dir = Directory.CreateTempSubdirectory("grimoira-http-parity-mem-").FullName;
        File.WriteAllText(Path.Combine(dir, $"{MemoryKey(label)}.md"),
            $"---\ntype: feedback\ntitle: {label}\nhook: {label}\n---\n\nA memory about {label} topic.\n");
        new IndexMemoryTool().Execute(connection, dir);
    }

    private static void SeedDocs(SqliteConnection connection, string label)
    {
        string dir = Directory.CreateTempSubdirectory("grimoira-http-parity-docs-").FullName;
        File.WriteAllText(Path.Combine(dir, $"{label}.md"), $"# {label}\n\nContent about {label} topic.\n");
        new IndexDocsTool().Execute(connection, dir, "doc");
    }

    private async Task<McpClient> ConnectAsync(HttpClient httpClient, string instance)
    {
        HttpClientTransportOptions options = new()
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Host"] = _allowedHost,
                [RequestProjectResolver.InstanceHeader] = instance,
            },
        };
        return await McpClient.CreateAsync(new HttpClientTransport(options, httpClient));
    }

    private static async Task<string> CallText(McpClient client, string toolName, object args)
    {
        Dictionary<string, object?> arguments = args.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(args));
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        return result.Content.Count > 0 && result.Content[0] is TextContentBlock text ? text.Text : "";
    }

    // History's rows carry a real insertion timestamp, which differs by construction between the old
    // and new store (two separate seed calls, milliseconds apart) even though the rest of the row is
    // identical; blank it out before comparing shape (same rule as McpSnapshotParityTests).
    internal static string StripTimestamps(string text) =>
        IsoTimestamp().Replace(text, "<ts>");

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        string dir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex IsoTimestamp();
}
