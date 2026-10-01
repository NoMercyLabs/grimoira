using Grimoira.Store.Schema;
using Microsoft.Data.Sqlite;

namespace Grimoira.Graph.Schema;

/// <summary>
/// RESTRUCTURE.md phase 3 slice 31 ("Graph paths become project-relative", rule 2026-09-25:
/// "relative"). Adds <c>edges.file_rel</c> — the path relative to the row's project root, forward-slash
/// separated, Linux-safe (<see cref="Path.GetRelativePath"/>) — alongside the existing <c>edges.file</c>
/// (kept until slice 33). Not folded into <see cref="GraphSchema"/>'s base <c>CREATE TABLE</c>: doing so
/// would add a column <c>WholeStoreSchemaDdlParityTests</c> never sees from grimoira.cs's own
/// <c>Init()</c>/<c>InitBrain()</c>, breaking that byte-identical-fresh-store oracle. Instead this is a
/// standalone, additive step applied through <see cref="SchemaRunner.Run"/> (backup-first, rule 3:
/// additive and idempotent) wherever a writer needs the column — today <c>IndexCodeTool</c>.
///
/// SQLite here has no <c>ALTER TABLE ... ADD COLUMN IF NOT EXISTS</c> (added upstream in 3.37; not
/// available in this bundle — a second bare <c>ADD COLUMN</c> throws "duplicate column name"), and
/// <see cref="SchemaRunner.Run"/> re-applies every provider's statements on every call regardless of the
/// <c>meta</c> record. So the column-exists check happens once, at construction, against the connection
/// this instance will run against, and <see cref="Statements"/> is empty on every call after the first.
/// </summary>
public sealed class GraphFileRelSchema(SqliteConnection connection) : ISchemaProvider
{
    public string Name => "GraphFileRel";

    public IReadOnlyList<string> Statements { get; } = HasColumn(connection, "edges", "file_rel")
        ? Array.Empty<string>()
        : ["ALTER TABLE edges ADD COLUMN file_rel TEXT"];

    public static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The value to store in <c>file_rel</c> for a (root, file) pair. <paramref name="file"/> already
    /// relative (today's convention for candidate/promoted rows, ExtractEdgesTool.cs:64) is returned
    /// forward-slashed as-is. An absolute <paramref name="file"/> under <paramref name="root"/> (today's
    /// convention for index-code decl rows, IndexCodeTool.cs:157) is made relative with
    /// <see cref="Path.GetRelativePath"/>. An absolute file NOT under root returns null — never guessed.
    /// </summary>
    public static string? ToRelative(string root, string file)
    {
        string normFile = file.Replace('\\', '/');
        if (!Path.IsPathRooted(file)) return normFile;

        string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        return rel.StartsWith("..") || Path.IsPathRooted(rel) ? null : rel;
    }

    /// <summary>The full path for THIS machine: <paramref name="projectRoot"/> joined with
    /// <paramref name="fileRel"/> when both are known, else the legacy <paramref name="file"/> value —
    /// the reader-side half of the same slice ("falling back to file where file_rel is NULL").</summary>
    public static string ResolveFull(string? projectRoot, string? fileRel, string file) =>
        !string.IsNullOrEmpty(fileRel) && !string.IsNullOrEmpty(projectRoot)
            ? Path.Combine(projectRoot, fileRel).Replace('\\', '/')
            : file;

    public static Dictionary<string, string> LoadProjectRoots(SqliteConnection connection)
    {
        Dictionary<string, string> roots = new(StringComparer.Ordinal);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, root FROM projects";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) roots[r.GetString(0)] = r.GetString(1);
        return roots;
    }

    public sealed record BackfillResult(int Total, int Filled, int OutsideRoot);

    /// <summary>
    /// One-time fill for existing rows: every <c>edges</c> row with <c>file_rel IS NULL</c> gets it
    /// computed from its own project's root. A row whose file is not under its project root, or whose
    /// project is not registered, is left NULL and counted in <see cref="BackfillResult.OutsideRoot"/>
    /// rather than guessed. Idempotent: a second call only ever touches rows still NULL, so an
    /// outside-root row stays NULL and an already-filled row is never revisited.
    /// </summary>
    public static BackfillResult Backfill(SqliteConnection connection)
    {
        Dictionary<string, string> roots = LoadProjectRoots(connection);
        List<(long id, string? project, string file)> rows = [];
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, project, file FROM edges WHERE file_rel IS NULL";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
        }

        int filled = 0, outside = 0;
        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach ((long id, string? project, string file) in rows)
        {
            string? rel = project is not null && roots.TryGetValue(project, out string? root)
                ? ToRelative(root, file)
                : null;
            if (rel is null) { outside++; continue; }

            using SqliteCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE edges SET file_rel=$r WHERE id=$id";
            update.Parameters.AddWithValue("$r", rel);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            filled++;
        }
        transaction.Commit();
        return new BackfillResult(rows.Count, filled, outside);
    }
}
