using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Facts.Tools;

/// <summary>
/// Logs one finding: an unrelated issue spotted during other work, surfaced to the operator later.
/// CLI verb <c>finding</c> and MCP tool <c>log_finding</c> are one job with two shapes (RESTRUCTURE.md
/// section 2.2), same pattern as <c>QueryTool</c>. <c>ExecuteCli</c> is copied verbatim from grimora.cs's
/// <c>AddFinding</c> (grimora.cs:673, logs to the mutation log); <c>ExecuteMcp</c> from mcp.cs's
/// <c>log_finding</c> (mcp.cs:887, does not log to the mutation log — kept as-is, not a new behaviour).
/// </summary>
public sealed class FindingTool : ITool
{
    public string Name => "finding";
    public string CliVerb => "finding";
    public string McpName => "log_finding";
    public string Help =>
        "finding <title> [--detail <d>] [--source <s>]   log an unrelated finding for later. " +
        "MCP log_finding(title, detail, source): same, no mutation-log entry.";

    public string ExecuteCli(SqliteConnection connection, string title, string detail, string source)
    {
        // Secrets in outputs (docs/RESTRUCTURE.md): title/detail are free text an LLM wrote, same shape as
        // a chat message IndexChatTool already scrubs.
        (title, _) = SecretScrubber.Redact(title);
        (detail, _) = SecretScrubber.Redact(detail);
        string ts = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        using (SqliteCommand begin = connection.CreateCommand()) { begin.CommandText = "BEGIN"; begin.ExecuteNonQuery(); }
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO findings(ts,title,detail,source,status) VALUES($t,$ti,$d,$s,'open')";
            insert.Parameters.AddWithValue("$t", ts);
            insert.Parameters.AddWithValue("$ti", title);
            insert.Parameters.AddWithValue("$d", detail);
            insert.Parameters.AddWithValue("$s", source);
            insert.ExecuteNonQuery();
        }
        MutationLog.Append(connection, "finding", title, "insert", null, "open", source);
        using (SqliteCommand commit = connection.CreateCommand()) { commit.CommandText = "COMMIT"; commit.ExecuteNonQuery(); }
        return "finding logged.";
    }

    public string ExecuteMcp(SqliteConnection connection, string title, string detail, string source)
    {
        (title, _) = SecretScrubber.Redact(title);
        (detail, _) = SecretScrubber.Redact(detail);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO findings(ts,title,detail,source,status) VALUES($t,$ti,$d,$s,'open')";
        command.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$ti", title);
        command.Parameters.AddWithValue("$d", detail);
        command.Parameters.AddWithValue("$s", source);
        command.ExecuteNonQuery();
        return $"finding logged: {title}";
    }
}
