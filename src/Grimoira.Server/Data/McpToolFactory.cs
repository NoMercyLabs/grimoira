using System.Reflection;
using Grimoira.Brain.Data;
using Grimoira.Server.Handover;
using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Grimoira.Server.Data;

/// <summary>
/// Maps the 25 golden MCP tools onto <see cref="McpServerTool"/> through one path (RESTRUCTURE.md
/// "Slice 26": "Map them through one registry, not 25 hand-written endpoints"). The 23 store-backed
/// tools (an <c>ExecuteMcp(SqliteConnection, ...)</c> method) go through the same
/// <see cref="BuildStoreBackedTool"/> reflection path, which resolves the connection parameter from the
/// calling project's <see cref="ProjectStore"/> instead of the JSON arguments and serialises every call
/// through <see cref="LockingAiFunction"/>. The 2 handover tools (<see cref="WorkspaceCapabilitiesTool"/>, <see cref="WorkspaceSearchTool"/>) hold no project store —
/// they take server-level dependencies (a process runner, the data/project directories) instead of a
/// connection, so they are wired directly to their own <c>Execute</c> overload.
/// </summary>
public static class McpToolFactory
{
    public static IReadOnlyList<McpServerTool> BuildTools(
        ToolRegistry registry, ProjectStore store, IHttpContextAccessor httpContextAccessor,
        string projectRoot)
    {
        IProcessRunner runner = new ProcessRunner();
        List<McpServerTool> tools = [];
        foreach (ITool tool in registry.Tools)
        {
            if (tool.McpName is null) continue;
            McpServerTool built = tool switch
            {
                WorkspaceCapabilitiesTool capabilities => BuildWorkspaceCapabilitiesTool(capabilities, projectRoot, runner, store, httpContextAccessor),
                WorkspaceSearchTool search => BuildWorkspaceSearchTool(search, projectRoot, runner, store, httpContextAccessor),
                _ => BuildStoreBackedTool(tool, store, httpContextAccessor),
            };
            tools.Add(built);
        }
        return tools;
    }

    /// <summary>
    /// The same 25 tools as plain <see cref="AIFunction"/>s (name, description, input schema, invoke) for the
    /// service routes <c>/tools</c> (RESTRUCTURE.md Slice P1), which `grimoira mcp` forwards to. Store-backed tools
    /// share <see cref="BuildStoreBackedFunction"/> with <see cref="BuildTools"/>, so both routes run under the
    /// one per-project writer gate; the two handover tools share their invoke delegates with it too.
    /// </summary>
    public static IReadOnlyList<AIFunction> BuildFunctions(
        ToolRegistry registry, ProjectStore store, IHttpContextAccessor httpContextAccessor,
        string projectRoot, TimeSpan? gateTimeout = null)
    {
        IProcessRunner runner = new ProcessRunner();
        List<AIFunction> functions = [];
        foreach (ITool tool in registry.Tools)
        {
            if (tool.McpName is null) continue;
            functions.Add(tool switch
            {
                WorkspaceCapabilitiesTool capabilities => PlainFunction(capabilities.McpName, capabilities.Help, CapabilitiesInvoker(capabilities, projectRoot, runner, store, httpContextAccessor)),
                WorkspaceSearchTool search => PlainFunction(search.McpName, search.Help, SearchInvoker(search, projectRoot, runner, store, httpContextAccessor)),
                _ => BuildStoreBackedFunction(tool, store, httpContextAccessor, gateTimeout ?? DefaultGateTimeout),
            });
        }
        return functions;
    }

    private static AIFunction PlainFunction(string name, string description, Delegate invoke) =>
        AIFunctionFactory.Create(invoke, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            ExcludeResultSchema = true,
            MarshalResult = (result, _, _) => new ValueTask<object?>(result),
        });

    /// <summary>
    /// The generic path for the 22 store-backed tools. Uses the real <c>ExecuteMcp</c>
    /// <see cref="MethodInfo"/> so the generated JSON schema keeps the tool's own parameter names and
    /// defaults; the leading <see cref="SqliteConnection"/> parameter is excluded from that schema and
    /// bound at call time from the requesting session's project store instead.
    /// </summary>
    private static McpServerTool BuildStoreBackedTool(ITool tool, ProjectStore store, IHttpContextAccessor httpContextAccessor) =>
        McpServerTool.Create(BuildStoreBackedFunction(tool, store, httpContextAccessor),
            new McpServerToolCreateOptions { Name = tool.McpName, Description = tool.Help });

    /// <summary>How long /tools waits for a project gate before answering 503 (the same as /cli's default).</summary>
    public static readonly TimeSpan DefaultGateTimeout = TimeSpan.FromSeconds(60);

    private static AIFunction BuildStoreBackedFunction(ITool tool, ProjectStore store, IHttpContextAccessor httpContextAccessor, TimeSpan? gateTimeout = null)
    {
        MethodInfo method = tool.GetType().GetMethod("ExecuteMcp")
            ?? throw new InvalidOperationException($"{tool.GetType().Name} has McpName '{tool.McpName}' but no ExecuteMcp method.");

        AIFunctionFactoryOptions options = new()
        {
            Name = tool.McpName,
            Description = tool.Help,
            // Every ExecuteMcp method returns a plain string the client renders as text. Left at its
            // default, AIFunctionFactory's MarshalResult always round-trips the return value through
            // JsonElement (AIFunction.InvokeAsync never hands back the raw string), so the MCP SDK's
            // AIFunctionMcpServerTool never takes its "obj is string" fast path and instead
            // JSON-serialises the JsonElement into the text content — a quoted, escaped copy of the
            // string mcp.dll (stdio) returns byte for byte (RESTRUCTURE.md "Slice 26b" parity finding).
            // Passing the result through unchanged restores that fast path.
            ExcludeResultSchema = true,
            MarshalResult = (result, _, _) => new ValueTask<object?>(result),
            ConfigureParameterBinding = parameter => parameter.ParameterType == typeof(SqliteConnection)
                ? new AIFunctionFactoryOptions.ParameterBindingOptions
                {
                    ExcludeFromSchema = true,
                    BindParameter = (_, args) =>
                        args.Context is not null && args.Context.TryGetValue(LockingAiFunction.ReaderKey, out object? reader) && reader is SqliteConnection readerConnection
                            ? readerConnection
                            : store.Acquire(RequestProjectResolver.Resolve(httpContextAccessor.HttpContext)).Connection,
                }
                : parameter.ParameterType == typeof(string) && parameter.Name is "sessionId" or "session"
                    // brain_stage/brain_flush's session id must come from the transport, never from the
                    // caller's own JSON arguments (a caller could otherwise name any session's ledger and
                    // flush it) — same reasoning as the SqliteConnection binding above: excluded from the
                    // tool's schema entirely and bound from the one place a session identity can come
                    // from, the request's own "Grimoira-Session" header (RequestSessionResolver).
                    ? new AIFunctionFactoryOptions.ParameterBindingOptions
                    {
                        ExcludeFromSchema = true,
                        BindParameter = (_, args) => RequestSessionResolver.Resolve(httpContextAccessor.HttpContext),
                    }
                    : default,
        };

        AIFunction inner = AIFunctionFactory.Create(method, tool, options);
        return new LockingAiFunction(inner, store, httpContextAccessor, tool.IsReadOnly, gateTimeout ?? DefaultGateTimeout);
    }

    private static McpServerTool BuildWorkspaceCapabilitiesTool(WorkspaceCapabilitiesTool tool, string projectRoot, IProcessRunner runner, ProjectStore store, IHttpContextAccessor context) =>
        McpServerTool.Create(CapabilitiesInvoker(tool, projectRoot, runner, store, context),
            new McpServerToolCreateOptions { Name = tool.McpName, Description = tool.Help });

    private static McpServerTool BuildWorkspaceSearchTool(WorkspaceSearchTool tool, string projectRoot, IProcessRunner runner, ProjectStore store, IHttpContextAccessor context) =>
        McpServerTool.Create(SearchInvoker(tool, projectRoot, runner, store, context),
            new McpServerToolCreateOptions { Name = tool.McpName, Description = tool.Help });

    private static Func<string, string> CapabilitiesInvoker(WorkspaceCapabilitiesTool tool, string projectRoot, IProcessRunner runner, ProjectStore store, IHttpContextAccessor context)
    {
        string Invoke(string query) => tool.Execute(query, ResolveWorkspaceRoot(projectRoot, store, context), runner);
        return Invoke;
    }

    private static Func<string, string, string, bool, string> SearchInvoker(WorkspaceSearchTool tool, string projectRoot, IProcessRunner runner, ProjectStore store, IHttpContextAccessor context)
    {
        string Invoke(string repository, string pattern, string path = "", bool names = false) =>
            tool.Execute(repository, pattern, path, names, ResolveWorkspaceRoot(projectRoot, store, context), runner);
        return Invoke;
    }

    private static string ResolveWorkspaceRoot(string configured, ProjectStore store, IHttpContextAccessor context)
    {
        string requestRoot = RequestProjectResolver.ProjectDirHeaderOf(context.HttpContext) ?? configured;
        ProjectHandle handle = store.Acquire(RequestProjectResolver.Resolve(context.HttpContext));
        using SqliteConnection reader = handle.OpenReader();
        return WorkspaceRootResolver.Resolve(requestRoot, reader);
    }
}
