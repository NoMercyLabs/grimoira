using System.Reflection;

namespace Grimora.Brain.Tests;

/// <summary>Invokes a static method on today's compiled <c>bin/mcp.dll</c> <c>GrimoraTools</c> class — the
/// oracle every brain MCP tool test runs against. Same pattern as ImpactToolTests' InvokeMcpImpact.</summary>
internal static class McpDll
{
    public static object? Invoke(string methodName, params object?[] args)
    {
        Assembly mcp = Assembly.LoadFrom(FindMcpDll());
        Type tools = mcp.GetType("GrimoraTools") ?? throw new InvalidOperationException("GrimoraTools type not found in mcp.dll");
        MethodInfo method = tools.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"GrimoraTools.{methodName} not found in mcp.dll");
        return method.Invoke(null, args);
    }

    private static string FindMcpDll()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "bin", "mcp.dll");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"bin/mcp.dll not found above {AppContext.BaseDirectory} — run build-mcp.ps1 first");
    }
}
