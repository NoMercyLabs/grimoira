using Grimora.Brain.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// Bulk write-last: load a pipe-delimited knowledge file in ONE transaction. Blank/<c>#</c> lines skipped.
/// Order nodes BEFORE the triples/slots that reference them (triggers reject dangling subjects/frames).
/// Idempotent: the shared <see cref="BrainWriters"/> writers supersede-or-noop, so re-running re-confirms
/// safely. CLI-only sub-verb (no MCP counterpart, RESTRUCTURE.md section 2.2). Copied verbatim from
/// grimora.cs's <c>BrainLearnBatch</c> (grimora.cs:2031).
/// </summary>
public sealed class BrainLearnBatchTool : ITool
{
    public string Name => "brain learn-batch";
    public string CliVerb => "brain learn-batch";
    public string? McpName => null;
    public string Help =>
        "brain learn-batch --from <file>        load a pipe-delimited file of nodes/triples/slots in one " +
        "transaction: node|k|kind|label|gloss|hard|scheme, triple|s|p|o|because, slot|frame|name|value|facet|multi.";

    /// <param name="stderr">Where skipped-line messages go. Defaults to <see cref="Console.Error"/>
    /// (RESTRUCTURE.md slice 29a) so its one caller, grimora.cs, sees today's output unchanged.</param>
    public string ExecuteCli(SqliteConnection connection, string file, TextWriter? stderr = null)
    {
        if (!File.Exists(file)) return $"file not found: {file}";

        TextWriter target = stderr ?? Console.Error;
        int nodes = 0, triples = 0, slots = 0, bad = 0;
        BeginTransaction(connection);
        foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] f = [.. line.Split('|').Select(x => x.Trim())];
            try
            {
                switch (f[0])
                {
                    case "node" when f.Length >= 4:
                        BrainWriters.AddNode(connection, f[1], f[2], f[3], f.Length > 4 ? f[4] : "", f.Length > 6 ? f[6] : "", f.Length > 5 && f[5] == "1", "learn-batch");
                        nodes++;
                        break;
                    case "triple" when f.Length >= 4:
                        BrainWriters.AddTriple(connection, f[1], f[2], f[3], f.Length > 4 ? f[4] : "", "learn-batch", false, "learn-batch", target);
                        triples++;
                        break;
                    case "slot" when f.Length >= 4:
                        BrainWriters.AddSlot(connection, f[1], f[2], f[3], f.Length > 4 ? f[4] : "text", f.Length > 5 && f[5] == "1", "", "learn-batch", "learn-batch");
                        slots++;
                        break;
                    default:
                        target.WriteLine($"skip (malformed): {line}");
                        bad++;
                        break;
                }
            }
            catch (SqliteException e)
            {
                target.WriteLine($"skip ({e.Message}): {line}");
                bad++;
            }
        }
        CommitTransaction(connection);

        return $"learn-batch: {nodes} nodes, {triples} triples, {slots} slots ({bad} skipped).";
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
