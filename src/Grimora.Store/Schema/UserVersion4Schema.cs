namespace Grimora.Store.Schema;

/// <summary>
/// RESTRUCTURE.md section 3.2, rule 3 and rule 7: <c>PRAGMA user_version</c> stays 3 for the whole of
/// phase 2 — bumping it while grimora.cs's old CLI (or mcp.cs, or launch-mcp.mjs) can still open the same
/// store would ping-pong it back to 3 on their very next open (grimora.cs:40-46's own init check), because
/// they still treat "not 3" as "needs my v3 init". Rule 7: "After phase 3, when only the new server
/// opens stores, <c>user_version</c> moves to 4 in one final step. From then on the runner owns the
/// version."
///
/// The gate: this step is a no-op — <see cref="Statements"/> is empty — unless the environment variable
/// <see cref="Gate"/> ("<c>Grimora_PHASE3_COMPLETE</c>") is set to <c>"1"</c>. Nothing in this repo sets
/// it, and nothing in this repo's pipelines (<c>IndexCodeSessionEndTool</c>, <c>InitFull</c>, or any
/// other <see cref="SchemaRunner.Run"/> call site) applies this provider yet. It exists, ready, for
/// slice 33 ("Cleanup" — deletes grimora.cs, mcp.cs, launch-mcp.mjs) to flip the gate on and wire this step
/// in, once those binaries are actually gone; until then the gate stays off and this class is inert.
/// </summary>
public sealed class UserVersion4Schema : ISchemaProvider
{
    /// <summary>The environment variable that must be <c>"1"</c> for this step to do anything.</summary>
    public const string Gate = "Grimora_PHASE3_COMPLETE";

    public string Name => "UserVersion4";

    public IReadOnlyList<string> Statements { get; }

    public UserVersion4Schema() : this(Environment.GetEnvironmentVariable(Gate)) { }

    /// <summary>Testable overload: pass the gate value directly rather than through the environment.</summary>
    public UserVersion4Schema(string? gateValue)
    {
        Statements = gateValue == "1" ? ["PRAGMA user_version = 4;"] : Array.Empty<string>();
    }
}
