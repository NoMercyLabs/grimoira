namespace Grimora.Store.Tools;

/// <summary>
/// The tool contract (RESTRUCTURE.md section 1): one file per tool, its help text is its documentation
/// (the MCP description, the CLI <c>--help</c> line and the docs, all in one), and a registry finds it.
/// A tool holds no LLM logic of its own — it is plain code over the store.
/// </summary>
public interface ITool
{
    /// <summary>The canonical name used for the registry test and for pairing a CLI verb with its MCP
    /// tool where both name the same job (RESTRUCTURE.md section 2.2).</summary>
    string Name { get; }

    /// <summary>The CLI verb this tool answers to, e.g. <c>"history"</c>.</summary>
    string CliVerb { get; }

    /// <summary>The MCP tool name this tool answers to, or <c>null</c> when the tool has no MCP side.</summary>
    string? McpName { get; }

    /// <summary>The one help text: what the tool does, its parameters, and how to call it — the MCP
    /// description, the CLI <c>--help</c> line and the documentation, all in one place.</summary>
    string Help { get; }
}
