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

    [Fact]
    public async Task AProjectGateHeldPastTheLimitIsServiceUnavailableProjectBusy()
    {
        using ProjectStore store = new(_dataDir);
        HttpContextAccessor accessor = new();
        IReadOnlyList<AIFunction> tools = McpToolFactory.BuildFunctions(
            AllMcpTools.BuildRegistry(), store, accessor, _dataDir, gateTimeout: TimeSpan.FromMilliseconds(300));
        DefaultHttpContext context = Request("{\"term\":\"anything\"}");
        context.Request.Headers[RequestProjectResolver.InstanceHeader] = "busy-project";
        accessor.HttpContext = context;
        ProjectHandle handle = store.Acquire("busy-project");
        handle.Gate.Wait();
        try
        {
            (int status, string body) = Answer(await ToolsEndpoint.Call("history", context, tools));

            Assert.Equal(503, status);
            Assert.Contains("project busy", body);
            Assert.DoesNotContain('\n', body);
        }
        finally
        {
            handle.Gate.Release();
        }
    }
}
