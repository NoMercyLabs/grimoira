using System.Text;
using System.Text.Json;
using Grimoira.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.AI;
using Xunit;

namespace Grimoira.Server.Tests;

// POST /tools/{name}: 400 only when the arguments cannot be bound to the tool parameters; a tool that throws
// is 500 with a one-line body; a project gate held past the limit is 503 "project busy".
public sealed class ToolsEndpointStatusTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("grimoira-tools-status-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private static DefaultHttpContext Request(string json, CancellationToken aborted = default)
    {
        return new DefaultHttpContext
        {
            Request = { Body = new MemoryStream(Encoding.UTF8.GetBytes(json)) },
            RequestAborted = aborted,
        };
    }

    private static string Throw(Exception exception) => throw exception;

    private static AIFunction Fake(string name, Delegate body) => AIFunctionFactory.Create(body, name);

    private static (int Status, string Body) Answer(IResult result)
    {
        ContentHttpResult content = Assert.IsType<ContentHttpResult>(result);
        return (content.StatusCode ?? 200, content.ResponseContent ?? "");
    }

    [Fact]
    public async Task ArgumentsOfTheWrongTypeAreBadRequest()
    {
        AIFunction[] tools = [Fake("count", (int n) => $"n={n}")];

        (int status, string body) = Answer(await ToolsEndpoint.Call("count", Request("{\"n\":[1,2]}"), tools));

        Assert.Equal(400, status);
        Assert.DoesNotContain('\n', body);
    }

    [Fact]
    public async Task AMissingRequiredArgumentIsBadRequest()
    {
        AIFunction[] tools = [Fake("count", (int n) => $"n={n}")];

        Assert.Equal(400, Answer(await ToolsEndpoint.Call("count", Request("{}"), tools)).Status);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(JsonException))]
    [InlineData(typeof(ArgumentException))]
    public async Task AToolThatThrowsInsideItselfIsAnInternalErrorWithAOneLineBody(Type exceptionType)
    {
        AIFunction[] tools = [Fake("boom", (string q) => Throw((Exception)Activator.CreateInstance(exceptionType, "inner\nsecond line")!))];

        (int status, string body) = Answer(await ToolsEndpoint.Call("boom", Request("{\"q\":\"x\"}"), tools));

        Assert.Equal(500, status);
        Assert.False(string.IsNullOrWhiteSpace(body));
        Assert.DoesNotContain('\n', body);
    }

    [Fact]
    public async Task ACancellationNotCausedByTheClientIsAnInternalErrorWithABody()
    {
        AIFunction[] tools = [Fake("cancelled", (string q) => Throw(new OperationCanceledException("gave up inside")))];

        (int status, string body) = Answer(await ToolsEndpoint.Call("cancelled", Request("{\"q\":\"x\"}"), tools));

        Assert.Equal(500, status);
        Assert.False(string.IsNullOrWhiteSpace(body));
    }

    private (IReadOnlyList<AIFunction> Tools, DefaultHttpContext Context, ProjectHandle Handle) BusyProject(ProjectStore store, string json)
    {
        HttpContextAccessor accessor = new();
        IReadOnlyList<AIFunction> tools = McpToolFactory.BuildFunctions(
            AllMcpTools.BuildRegistry(), store, accessor, _dataDir, gateTimeout: TimeSpan.FromMilliseconds(300));
        DefaultHttpContext context = Request(json);
        context.Request.Headers[RequestProjectResolver.InstanceHeader] = "busy-project";
        accessor.HttpContext = context;
        return (tools, context, store.Acquire("busy-project"));
    }

    [Fact]
    public async Task AWriteToolOnAProjectGateHeldPastTheLimitIsServiceUnavailableProjectBusy()
    {
        using ProjectStore store = new(_dataDir);
        (IReadOnlyList<AIFunction> tools, DefaultHttpContext context, ProjectHandle handle) =
            BusyProject(store, "{\"title\":\"t\",\"detail\":\"d\",\"source\":\"s\"}");
        handle.Gate.Wait();
        try
        {
            (int status, string body) = Answer(await ToolsEndpoint.Call("log_finding", context, tools));

            Assert.Equal(503, status);
            Assert.Contains("project busy", body);
            Assert.DoesNotContain('\n', body);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    // Issue #24: a read-only tool must answer while a write (or a session-end index job) holds the gate.
    [Theory]
    [InlineData("history", "{\"term\":\"anything\"}")]
    [InlineData("recall", "{\"query\":\"anything\"}")]
    [InlineData("graph_query", "{\"question\":\"anything\"}")]
    [InlineData("doc", "{\"query\":\"anything\"}")]
    public async Task AReadOnlyToolAnswersWhileTheProjectGateIsHeld(string tool, string json)
    {
        using ProjectStore store = new(_dataDir);
        (IReadOnlyList<AIFunction> tools, DefaultHttpContext context, ProjectHandle handle) = BusyProject(store, json);
        handle.Gate.Wait();
        try
        {
            (int status, string body) = Answer(await ToolsEndpoint.Call(tool, context, tools));

            Assert.Equal(200, status);
            Assert.DoesNotContain("project busy", body);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    [Theory]
    [InlineData("workspace_search", "{\"repository\":\"missing\",\"pattern\":\"needle\"}")]
    [InlineData("workspace_capabilities", "{\"query\":\"anything\"}")]
    public async Task AWorkspaceToolAnswersWhileTheProjectGateIsHeld(string tool, string json)
    {
        using ProjectStore store = new(_dataDir);
        (IReadOnlyList<AIFunction> tools, DefaultHttpContext context, ProjectHandle handle) = BusyProject(store, json);
        handle.Gate.Wait();
        try
        {
            Task<IResult> call = ToolsEndpoint.Call(tool, context, tools);
            IResult result = await call.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(200, Answer(result).Status);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    [Fact]
    public async Task AHookFailsOpenWhenTheProjectGateStaysBusy()
    {
        using ProjectStore store = new(_dataDir);
        ProjectHandle handle = store.Acquire("busy-project");
        DefaultHttpContext context = Request("{\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"x.txt\"}}");
        context.Request.Headers[RequestProjectResolver.InstanceHeader] = "busy-project";
        IdleExit idle = new(TimeSpan.FromMinutes(1), () => { });
        IndexJobQueue queue = new(store, idle);
        handle.Gate.Wait();
        try
        {
            IResult result = await HookEndpoint.Handle("PreToolUse", context, store, queue)
                .WaitAsync(HookEndpoint.GateTimeout + TimeSpan.FromSeconds(2));
            Assert.Equal(200, Answer(result).Status);
            Assert.Equal("", Answer(result).Body);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    [Fact]
    public async Task AnIndexJobStopsWaitingForABusyProjectGate()
    {
        using ProjectStore store = new(_dataDir);
        ProjectHandle handle = store.Acquire("busy-project");
        IdleExit idle = new(TimeSpan.FromMinutes(1), () => { });
        IndexJobQueue queue = new(store, idle, TimeSpan.FromMilliseconds(40));
        handle.Gate.Wait();
        try
        {
            queue.Enqueue("busy-project", "{}");
            _ = queue.RunAsync();
            await Task.Delay(500);
            Assert.Equal(0, idle.InFlight);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    [Fact]
    public void EveryStoreBackedMcpToolDeclaresWhetherItIsReadOnly()
    {
        string[] readOnly = [.. AllMcpTools.BuildRegistry().Tools
            .Where(t => t.McpName is not null && t.IsReadOnly).Select(t => t.McpName!).Order()];

        Assert.Equal(
        [
            "brain_common", "brain_core", "brain_gaps", "brain_impact", "brain_place", "brain_recall", "brain_scope",
            "chat_count", "chat_list", "doc", "fact", "graph_explain", "graph_path", "graph_query", "history",
            "impact", "open_findings", "patterns", "recall", "rule",
        ], readOnly);
    }
}
