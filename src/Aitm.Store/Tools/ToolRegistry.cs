namespace Aitm.Store.Tools;

/// <summary>
/// Finds a tool by the name a caller already has: the CLI verb it typed, or the MCP tool name the
/// client asked for. Holds no knowledge of what a tool does — that stays inside the tool.
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools) => _tools = [.. tools];

    public IReadOnlyList<ITool> Tools => _tools;

    public ITool? FindByCliVerb(string verb) =>
        _tools.FirstOrDefault(t => string.Equals(t.CliVerb, verb, StringComparison.Ordinal));

    public ITool? FindByMcpName(string name) =>
        _tools.FirstOrDefault(t => string.Equals(t.McpName, name, StringComparison.Ordinal));
}
