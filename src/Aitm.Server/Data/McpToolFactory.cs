using System.Reflection;
using Aitm.Brain.Data;
using Aitm.Server.Handover;
using Aitm.Store.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Aitm.Server.Data;

/// <summary>
/// Maps the 25 golden MCP tools onto <see cref="McpServerTool"/> through one path (RESTRUCTURE.md
/// "Slice 26": "Map them through one registry, not 25 hand-written endpoints"). The 22 store-backed
/// tools (an <c>ExecuteMcp(SqliteConnection, ...)</c> method) go through the same
/// <see cref="BuildStoreBackedTool"/> reflection path, which resolves the connection parameter from the
/// calling project's <see cref="ProjectStore"/> instead of the JSON arguments and serialises every call
/// through <see cref="LockingAIFunction"/>. The 3 handover tools (<see cref="IdPTokenTool"/>,
/// <see cref="WorkspaceCapabilitiesTool"/>, <see cref="WorkspaceSearchTool"/>) hold no project store —
/// they take server-level dependencies (a process runner, the data/project directories) instead of a
/// connection, so they are wired directly to their own <c>Execute</c> overload.
/// </summary>
public static class McpToolFactory
{
    public static IReadOnlyList<McpServerTool> BuildTools(
        ToolRegistry registry, ProjectStore store, IHttpContextAccessor httpContextAccessor,
        string projectRoot, string dataDir)
    {
        IProcessRunner runner = new ProcessRunner();
        List<McpServerTool> tools = [];
        foreach (ITool tool in registry.Tools)
        {
            if (tool.McpName is null) continue;
            McpServerTool built = tool switch
            {
                IdPTokenTool idp => BuildIdPTool(idp, dataDir, runner),
                WorkspaceCapabilitiesTool capabilities => BuildWorkspaceCapabilitiesTool(capabilities, projectRoot, runner),
                WorkspaceSearchTool search => BuildWorkspaceSearchTool(search, projectRoot, runner),
                _ => BuildStoreBackedTool(tool, store, httpContextAccessor),
            };
            tools.Add(built);
        }
        return tools;
    }

    /// <summary>
    /// The generic path for the 22 store-backed tools. Uses the real <c>ExecuteMcp</c>
    /// <see cref="MethodInfo"/> so the generated JSON schema keeps the tool's own parameter names and
    /// defaults; the leading <see cref="SqliteConnection"/> parameter is excluded from that schema and
    /// bound at call time from the requesting session's project store instead.
    /// </summary>
    private static McpServerTool BuildStoreBackedTool(ITool tool, ProjectStore store, IHttpContextAccessor httpContextAccessor)
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
                    BindParameter = (_, args) => store.Acquire(RequestProjectResolver.Resolve(httpContextAccessor.HttpContext)).Connection,
                }
                : default,
        };

        AIFunction inner = AIFunctionFactory.Create(method, tool, options);
        AIFunction locking = new LockingAIFunction(inner, store, httpContextAccessor);
        return McpServerTool.Create(locking, new McpServerToolCreateOptions { Name = tool.McpName, Description = tool.Help });
    }

    private static McpServerTool BuildIdPTool(IdPTokenTool tool, string dataDir, IProcessRunner runner)
    {
        string Invoke(string subject, string realm = "dev")
        {
            bool mintAllowed = Environment.GetEnvironmentVariable("AITM_ALLOW_TOKEN_MINT") == "1";
            string? engineScriptPath = EnginePath();
            return tool.Execute(subject, realm, engineScriptPath ?? "", dataDir, mintAllowed, runner);
        }
        return McpServerTool.Create((Func<string, string, string>)Invoke,
            new McpServerToolCreateOptions { Name = tool.McpName, Description = tool.Help });
    }

    private static McpServerTool BuildWorkspaceCapabilitiesTool(WorkspaceCapabilitiesTool tool, string projectRoot, IProcessRunner runner)
    {
        string Invoke(string query) => tool.Execute(query, projectRoot, runner);
        return McpServerTool.Create((Func<string, string>)Invoke,
            new McpServerToolCreateOptions { Name = tool.McpName, Description = tool.Help });
    }

    private static McpServerTool BuildWorkspaceSearchTool(WorkspaceSearchTool tool, string projectRoot, IProcessRunner runner)
    {
        string Invoke(string repository, string pattern, string path = "", bool names = false) =>
            tool.Execute(repository, pattern, path, names, projectRoot, runner);
        return McpServerTool.Create((Func<string, string, string, bool, string>)Invoke,
            new McpServerToolCreateOptions { Name = tool.McpName, Description = tool.Help });
    }

    // Mirrors mcp.cs's EnginePath(): locates idp-impersonate.mjs at AITM_HOME when set, in the plugin root
    // (slice 32a: the installed server runs from the data folder), or beside the running binary. Returns null
    // (never throws) so IdPTokenTool reports the same "cannot locate" message it always has for a missing engine.
    private static string? EnginePath() => PluginFileLocator.FindEngine("idp-impersonate.mjs",
        Environment.GetEnvironmentVariable("AITM_HOME"), PluginFileLocator.PluginRoot(), AppContext.BaseDirectory);
}
