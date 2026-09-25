using Aitm.Brain.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Tools;

/// <summary>
/// BRAIN/write-last: persist a durable node, triple, or slot. CLI verb <c>brain learn</c> and MCP tool
/// <c>brain_learn</c> are one job with two shapes today (RESTRUCTURE.md section 2.2). <c>ExecuteCli</c> is
/// copied verbatim from aitm.cs's <c>BrainLearn</c> (aitm.cs:1698): it is silent on success (nothing on
/// stdout) and only prints a guard rejection or the "unknown sub-verb" usage line. <c>ExecuteMcp</c> is
/// copied verbatim from mcp.cs's <c>brain_learn</c>/<c>LearnCore</c> (mcp.cs:782, mcp.cs:790) — its own
/// richer <see cref="NodeGuard.ValidateMcp"/>, its own return-a-sentence shape, kept apart from the CLI
/// side the same way <c>QueryTool</c> keeps its two shapes apart. Both sides now write through
/// <see cref="BrainWriters"/>, so both log to the mutation table — mcp.cs's own <c>LearnCore</c> never did.
/// </summary>
public sealed class BrainLearnTool : ITool
{
    public string Name => "brain learn";
    public string CliVerb => "brain learn";
    public string? McpName => "brain_learn";
    public string Help =>
        "brain learn node <k> <kind> <label> [--gloss ..] [--scheme ..] [--hard] | " +
        "brain learn triple <s> <predicate> <o> [--because ..] [--hard] | " +
        "brain learn slot <frame> <name> <value> [--facet ..] [--multi] [--because ..]. " +
        "MCP brain_learn(kind,key,a,b,c,because,hard): kind='node' -> key,a=nodekind,b=label,c=gloss; " +
        "kind='triple' -> key=subject,a=predicate,b=object; kind='slot' -> key=frame,a=name,b=value.";

    public string ExecuteCli(SqliteConnection connection, IReadOnlyList<string> args, string gloss = "", string scheme = "", string facet = "text", string because = "", bool hard = false, bool multi = false)
    {
        string what = args.Count > 0 ? args[0] : "";
        List<string> p = args.Skip(1).ToList();
        string result = "";

        BeginTransaction(connection);
        switch (what)
        {
            case "node":
                if (p.Count < 3) { result = "usage: brain learn node <k> <kind> <label> [--gloss ..] [--scheme ..] [--hard]"; break; }
                string guard = NodeGuard.ValidateCli(p[1], string.Join(' ', p.Skip(2)));
                if (guard.Length > 0) { result = guard; break; }
                BrainWriters.AddNode(connection, p[0], p[1], string.Join(' ', p.Skip(2)), gloss, scheme, hard, "brain learn");
                break;
            case "triple":
                if (p.Count < 3) { result = "usage: brain learn triple <s> <predicate> <o> [--because ..] [--hard]"; break; }
                BrainWriters.AddTriple(connection, p[0], p[1], p[2], because, "manual", hard, "brain learn");
                break;
            case "slot":
                if (p.Count < 3) { result = "usage: brain learn slot <frame> <name> <value> [--facet ..] [--multi] [--because ..]"; break; }
                BrainWriters.AddSlot(connection, p[0], p[1], string.Join(' ', p.Skip(2)), facet, multi, because, "manual", "brain learn");
                break;
            default:
                result = "brain learn <node|triple|slot> …";
                break;
        }
        CommitTransaction(connection);

        if (what is "node" or "triple" or "slot" && p.Count >= 3)
            BrainGapResolver.Resolve(connection, $"{string.Join(' ', p)} {gloss} {because}");

        return result;
    }

    public string ExecuteMcp(SqliteConnection connection, string kind, string key, string a = "", string b = "", string c = "", string because = "", bool hard = false)
    {
        string result = LearnCore(connection, kind, key, a, b, c, because, hard);
        if (!result.StartsWith("rejected", StringComparison.Ordinal)
            && !result.StartsWith("unknown", StringComparison.Ordinal)
            && !result.StartsWith("kind must", StringComparison.Ordinal))
            BrainGapResolver.Resolve(connection, $"{key} {a} {b} {c} {because}");
        return result;
    }

    private static string LearnCore(SqliteConnection connection, string kind, string key, string a, string b, string c, string because, bool hard)
    {
        try
        {
            switch (kind)
            {
                case "node":
                    string guardErr = NodeGuard.ValidateMcp(a, b.Length > 0 ? b : key);
                    if (guardErr.Length > 0) return guardErr;
                    // mcp.cs's own pre-slice-24 LearnCore never touched scheme (its INSERT had no scheme
                    // column, and its noop check never compared it), so a node whose scheme was already
                    // backfilled by MaybeMaintain() must not read as "changed" here just because this call
                    // passes no scheme of its own — read the live value forward instead of overwriting it
                    // with an empty one, so an otherwise-identical relearn still reports noop.
                    string currentScheme = CurrentScheme(connection, key);
                    bool changed = BrainWriters.AddNode(connection, key, a, b.Length > 0 ? b : key, c, currentScheme, hard, "brain_learn");
                    return changed ? "node learned." : "noop (unchanged).";
                case "triple":
                    BrainWriters.TripleWrite outcome = BrainWriters.AddTriple(connection, key, a, b, because, "mcp", hard, "brain_learn");
                    return outcome switch
                    {
                        BrainWriters.TripleWrite.UnknownPredicate => $"unknown predicate '{a}'.",
                        BrainWriters.TripleWrite.Reinforced => "reinforced (known relation, confidence bumped).",
                        _ => "triple learned.",
                    };
                case "slot":
                    BrainWriters.AddSlot(connection, key, a, b, "text", multi: false, because: because, src: "mcp", why: "brain_learn");
                    return "slot learned.";
                default:
                    return "kind must be node | triple | slot.";
            }
        }
        catch (SqliteException e)
        {
            return "rejected: " + e.Message;
        }
    }

    private static string CurrentScheme(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT scheme FROM node_now WHERE k=$k";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string ?? "";
    }

    private static void BeginTransaction(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "BEGIN";
        command.ExecuteNonQuery();
    }

    private static void CommitTransaction(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "COMMIT";
        command.ExecuteNonQuery();
    }
}
