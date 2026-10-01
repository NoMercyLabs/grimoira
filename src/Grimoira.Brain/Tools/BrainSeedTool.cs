using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Brain.Tools;

/// <summary>
/// Phase B of onboarding: replay the curated spine (projects, platforms, shared seams, contracts,
/// placement frames) into a fresh store. This is one ecosystem's own data, not engine behaviour, so it
/// is a thin wrapper over <see cref="SpineImportTool"/> with the extra hint text an operator needs when
/// no spine file exists yet. Copied verbatim from grimoira.cs's <c>BrainSeed</c> (grimoira.cs:2156). CLI-only
/// sub-verb (no MCP counterpart, RESTRUCTURE.md section 2.2).
/// </summary>
public sealed class BrainSeedTool : ITool
{
    public string Name => "brain seed";
    public string CliVerb => "brain seed";
    public string? McpName => null;
    public string Help =>
        "brain seed [--from <spine.json>]       load the curated spine (default <exe>/../seeds/spine.json); " +
        "run `grimoira spine-export` on an instance that already has one, or write the file by hand.";

    public string ExecuteCli(SqliteConnection connection, string seed)
    {
        if (!File.Exists(seed))
            return $"no spine file at {Path.GetFullPath(seed)} — run `grimoira spine-export` on an instance that already has one, or write the file by hand (see README).";
        return new SpineImportTool().ExecuteCli(connection, seed);
    }
}
