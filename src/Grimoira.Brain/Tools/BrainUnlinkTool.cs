using Grimoira.Store.Data;
using Grimoira.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimoira.Brain.Tools;

/// <summary>
/// Retract a single wrong edge (e.g. an inference that turned out false) without touching the nodes it
/// links. Copied verbatim from grimoira.cs's <c>BrainUnlink</c> (grimoira.cs:1742-1754). RESTRUCTURE.md section 5
/// ("Protecting the system from the agents it serves") says a CLI admin verb that deletes or bulk-changes
/// makes an automatic backup first — <c>unlink</c> retracts a live edge, so it takes a
/// <see cref="BackupTool"/> (VACUUM INTO) snapshot before it writes, the same way <c>forget-project</c> does.
/// </summary>
public sealed class BrainUnlinkTool : ITool
{
    public string Name => "brain unlink";
    public string CliVerb => "brain unlink";
    public string? McpName => null;
    public string Help => "brain unlink <subject> <predicate> <object>   retract a single wrong edge";

    public string Execute(SqliteConnection connection, string root, string s, string p, string o)
    {
        new BackupTool().Execute(connection, root, null);

        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM triple_now WHERE s=$s AND p=$p AND o=$o";
            count.Parameters.AddWithValue("$s", s);
            count.Parameters.AddWithValue("$p", p);
            count.Parameters.AddWithValue("$o", o);
            if ((long)(count.ExecuteScalar() ?? 0L) == 0)
                return $"no live triple '{s} {p} {o}'.";
        }

        string now = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE triple SET valid_to=$now WHERE s=$s AND p=$p AND o=$o AND valid_to IS NULL";
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$s", s);
            update.Parameters.AddWithValue("$p", p);
            update.Parameters.AddWithValue("$o", o);
            update.ExecuteNonQuery();
        }
        MutationLog.Append(connection, "triple", $"{s} {p} {o}", "unlink", $"{s}|{p}|{o}", null, "retracted");

        return $"unlinked {s} {p} {o}.";
    }
}
