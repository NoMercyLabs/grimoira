namespace Grimora.Store.Tools;

/// <summary>
/// Creates/opens the instance. Copied verbatim from grimora.cs's <c>init</c> case (grimora.cs:70-72): by the
/// time a tool runs, <c>StoreConnection.Open</c> has already created the instance directory and the
/// database file, so this only reports where it landed.
/// </summary>
public sealed class InitTool : ITool
{
    public string Name => "init";
    public string CliVerb => "init";
    public string? McpName => null;
    public string Help => "init                                create/open the instance";

    public string Execute(string instance, string dbPath) => $"instance '{instance}' ready at {dbPath}";
}
