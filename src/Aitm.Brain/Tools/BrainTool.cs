using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// BRAIN router — dispatches the <c>brain &lt;sub-verb&gt;</c> CLI command to the sub-verb's tool.
/// Copied verbatim from aitm.cs's <c>BrainCmd</c> switch (aitm.cs:1308): only the sub-verbs this slice
/// moves (<see cref="BrainCoreTool"/>, <see cref="BrainScopeTool"/>, <see cref="BrainCommonTool"/>,
/// <see cref="BrainPlaceTool"/>) are wired here; every other sub-verb — moved, unmoved, or genuinely
/// unknown — falls through to the same default line aitm.cs prints for an unrecognised sub-verb, until
/// its own slice moves it and wires it in here too. There is no MCP counterpart: over MCP each sub-verb
/// is already its own named tool (RESTRUCTURE.md section 2.2), so this router is CLI-only.
/// </summary>
public sealed class BrainTool : ITool
{
    private readonly BrainCoreTool _core = new();
    private readonly BrainScopeTool _scope = new();
    private readonly BrainCommonTool _common = new();
    private readonly BrainPlaceTool _place = new();

    public string Name => "brain";
    public string CliVerb => "brain";
    public string? McpName => null;
    public string Help =>
        "brain <core|scope <proj…>|common <proj…>|place <codekind>|recall <text>|impact <symbol>|learn …|gaps|distill|stats>";

    public string ExecuteCli(SqliteConnection connection, IReadOnlyList<string> args)
    {
        string sub = args.Count > 0 ? args[0] : "help";
        List<string> rest = args.Skip(1).ToList();
        return sub switch
        {
            "core" => _core.ExecuteCli(connection),
            "scope" => _scope.ExecuteCli(connection, rest),
            "common" => _common.ExecuteCli(connection, rest),
            "place" => _place.ExecuteCli(connection, rest.FirstOrDefault() ?? ""),
            _ => Help,
        };
    }
}
