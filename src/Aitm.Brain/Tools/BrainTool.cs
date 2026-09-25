using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// BRAIN router — dispatches the <c>brain &lt;sub-verb&gt;</c> CLI command to the sub-verb's tool.
/// Copied verbatim from aitm.cs's <c>BrainCmd</c> switch (aitm.cs:1308): only the sub-verbs moved so far
/// (slice 15's <see cref="BrainCoreTool"/>, <see cref="BrainScopeTool"/>, <see cref="BrainCommonTool"/>,
/// <see cref="BrainPlaceTool"/>; slice 16's <see cref="BrainRecallTool"/>, <see cref="BrainImpactTool"/>,
/// <see cref="BrainStatsTool"/>, <see cref="BrainGapsTool"/>, <see cref="BrainWhyTool"/>,
/// <see cref="BrainStaleTool"/>, <see cref="BrainAuditTool"/>) are wired here; every other sub-verb —
/// unmoved, or genuinely unknown — falls through to the same default line aitm.cs prints for an
/// unrecognised sub-verb, until its own slice moves it and wires it in here too. There is no MCP
/// counterpart: over MCP each sub-verb is already its own named tool (RESTRUCTURE.md section 2.2), so
/// this router is CLI-only.
/// </summary>
public sealed class BrainTool : ITool
{
    private readonly BrainCoreTool _core = new();
    private readonly BrainScopeTool _scope = new();
    private readonly BrainCommonTool _common = new();
    private readonly BrainPlaceTool _place = new();
    private readonly BrainRecallTool _recall = new();
    private readonly BrainImpactTool _impact = new();
    private readonly BrainStatsTool _stats = new();
    private readonly BrainGapsTool _gaps = new();
    private readonly BrainWhyTool _why = new();
    private readonly BrainStaleTool _stale = new();
    private readonly BrainAuditTool _audit = new();

    public string Name => "brain";
    public string CliVerb => "brain";
    public string? McpName => null;
    public string Help =>
        "brain <core|scope <proj…>|common <proj…>|place <codekind>|recall <text>|impact <symbol>|learn …|gaps|distill|stats>";

    public string ExecuteCli(SqliteConnection connection, IReadOnlyList<string> args) =>
        ExecuteCli(connection, args, instance: "", days: 30);

    // The full-featured overload: `brain audit` needs the instance name (StatsTool/InitTool's own
    // pattern — a value only the CLI's own --instance flag knows, threaded in rather than read from a
    // global) and `brain stale` needs the parsed --days flag (aitm.cs:1350's GetFlag("--days")).
    public string ExecuteCli(SqliteConnection connection, IReadOnlyList<string> args, string instance, int days)
    {
        string sub = args.Count > 0 ? args[0] : "help";
        List<string> rest = args.Skip(1).ToList();
        return sub switch
        {
            "core" => _core.ExecuteCli(connection),
            "scope" => _scope.ExecuteCli(connection, rest),
            "common" => _common.ExecuteCli(connection, rest),
            "place" => _place.ExecuteCli(connection, rest.FirstOrDefault() ?? ""),
            "recall" => _recall.ExecuteCli(connection, string.Join(' ', rest)),
            "impact" => _impact.ExecuteCli(connection, string.Join(' ', rest)),
            "stats" => _stats.Execute(connection),
            "gaps" => _gaps.ExecuteCli(connection),
            "why" => rest.Count < 1 ? "usage: brain why <node-key>" : _why.Execute(connection, rest[0]),
            "stale" => _stale.Execute(connection, days),
            "audit" => _audit.Execute(connection, instance),
            _ => Help,
        };
    }
}
