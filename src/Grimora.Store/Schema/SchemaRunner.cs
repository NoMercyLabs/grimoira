using Microsoft.Data.Sqlite;

namespace Grimora.Store.Schema;

/// <summary>
/// The outcome of <see cref="SchemaRunner.Run"/>: which providers finished, and — on failure — why the
/// run stopped and where the pre-step backup for the failing provider landed.
/// </summary>
public sealed record SchemaRunResult(bool Success, IReadOnlyList<string> AppliedProviders, string? Error, string? BackupPath);

/// <summary>
/// Store owns the runner; every project only ever hands it an <see cref="ISchemaProvider"/>, so a
/// reference the wrong way round shows up as a project reaching for the runner's internals rather
/// than as Store importing another project's schema (RESTRUCTURE.md section 1).
/// </summary>
public static class SchemaRunner
{
    /// <summary>Applies every provider's statements, in the given order, on the given connection, with
    /// no backup, no transaction and no meta bookkeeping. Used to build the sqlite_master parity oracle
    /// (slices 3a/3b) and as the "what a clean apply produces" comparison inside slice-3c tests.</summary>
    public static void Apply(SqliteConnection connection, IEnumerable<ISchemaProvider> providers)
    {
        foreach (ISchemaProvider provider in providers)
        {
            foreach (string statement in provider.Statements)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Applies every provider's statements against an already-open <paramref name="connection"/>, one
    /// provider at a time. RESTRUCTURE.md section 3.2, rule 5 ("backup before every step") and the
    /// migration path as a whole:
    /// <list type="number">
    /// <item><description>A pre-step backup is made with <c>VACUUM INTO</c> and checked with
    /// <c>PRAGMA integrity_check</c> before anything is touched.</description></item>
    /// <item><description>The provider's own statements run inside one transaction, so a statement that
    /// throws rolls back every earlier statement from the same provider — the store is left exactly as
    /// it was before this provider ran.</description></item>
    /// <item><description>Once a provider's statements apply cleanly, its step is recorded in
    /// <c>meta</c> under the key <c>schema:&lt;provider.Name&gt;</c> (rule 3), in the same transaction,
    /// so the record and the DDL either both land or neither does.</description></item>
    /// <item><description><c>PRAGMA user_version</c> is never written here — rule 3's ping-pong trap:
    /// while an old binary still stamps it to 3 on every open where it differs, only the DDL for each
    /// provider must be additive and idempotent.</description></item>
    /// </list>
    /// A failing provider stops the run; providers already applied earlier in the same call stay applied
    /// (they are additive `IF NOT EXISTS` statements, safe to have landed), and the ones after the
    /// failure never run.
    /// </summary>
    public static SchemaRunResult Run(SqliteConnection connection, IEnumerable<ISchemaProvider> providers, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        List<string> applied = [];
        foreach (ISchemaProvider provider in providers)
        {
            string backupPath = Path.Combine(
                backupDirectory,
                $"pre-{provider.Name}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.db");
            try
            {
                Backup(connection, backupPath);
                if (!IntegrityOk(backupPath))
                    return new SchemaRunResult(false, applied, $"backup integrity check failed for provider '{provider.Name}'", backupPath);

                ApplyOneProviderInTransaction(connection, provider);
                applied.Add(provider.Name);
            }
            catch (Exception ex)
            {
                return new SchemaRunResult(false, applied, ex.Message, backupPath);
            }
        }
        return new SchemaRunResult(true, applied, null, null);
    }

    private static void ApplyOneProviderInTransaction(SqliteConnection connection, ISchemaProvider provider)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            foreach (string statement in provider.Statements)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
            RecordStep(connection, transaction, provider.Name);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>Additive, idempotent: a second run for the same provider writes the same value again
    /// rather than failing or duplicating a row (rule 3, "additive and idempotent").</summary>
    private static void RecordStep(SqliteConnection connection, SqliteTransaction transaction, string providerName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO meta(key, value) VALUES ($key, '1') " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", $"schema:{providerName}");
        command.ExecuteNonQuery();
    }

    private static void Backup(SqliteConnection connection, string backupPath)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path";
        command.Parameters.AddWithValue("$path", backupPath);
        command.ExecuteNonQuery();
    }

    private static bool IntegrityOk(string backupPath)
    {
        using SqliteConnection copy = new($"Data Source={backupPath};Mode=ReadOnly");
        copy.Open();
        using SqliteCommand command = copy.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        return string.Equals(Convert.ToString(command.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase);
    }
}
