using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Brain.Tools;

/// <summary>
/// Data-quality backfill: give scheme-less nodes a sensible default (their kind) so scope grouping/
/// ordering is consistent. A live-row metadata update; the node_au trigger keeps FTS in sync. Copied
/// verbatim from grimora.cs's <c>BrainTidy</c> (grimora.cs:1899-1906). RESTRUCTURE.md section 5 says a delete/
/// bulk-change verb backs up first (design checklist; as forget-project) — <c>tidy</c> bulk-updates every
/// scheme-less node, so it takes the same <see cref="BackupTool"/> snapshot first.
/// </summary>
public sealed class BrainTidyTool : ITool
{
    public string Name => "brain tidy";
    public string CliVerb => "brain tidy";
    public string? McpName => null;
    public string Help => "brain tidy   backfill scheme=kind on scheme-less nodes";

    public string Execute(SqliteConnection connection, string root)
    {
        new BackupTool().Execute(connection, root, null);

        long n;
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM node_now WHERE scheme IS NULL OR scheme=''";
            n = (long)(count.ExecuteScalar() ?? 0L);
        }

        using (SqliteCommand update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE node SET scheme=kind WHERE valid_to IS NULL AND (scheme IS NULL OR scheme='')";
            update.ExecuteNonQuery();
        }

        return $"tidy: backfilled scheme=kind on {n} node(s).";
    }
}
