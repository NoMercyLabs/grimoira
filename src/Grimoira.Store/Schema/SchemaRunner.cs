using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Grimoira.Store.Schema;

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
    /// so the record and the DDL either both land or neither does. The value is the SHA-256 (lowercase
    /// hex) of the provider's statements joined by newline; a provider whose recorded hash equals its
    /// current one is skipped entirely (no backup, no apply), so an open of an unchanged store copies
    /// nothing. A store that still carries the earlier marker value <c>'1'</c> gets one more backup and
    /// apply per provider, and from then on the hash.</description></item>
    /// <item><description>Once every provider applied (a successful run only, never between a backup
    /// and its apply, never after a failure) only the newest 3 <c>pre-*.db</c> files by write time in
    /// <paramref name="backupDirectory"/> are kept; the live store is never touched.</description></item>
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
            string hash = StatementsHash(provider);
            string backupPath = Path.Combine(
                backupDirectory,
                $"pre-{provider.Name}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.db");
            try
            {
                if (string.Equals(RecordedHash(connection, provider.Name), hash, StringComparison.Ordinal))
                    continue;

                Backup(connection, backupPath);
                if (!IntegrityOk(backupPath))
                    return new SchemaRunResult(false, applied, $"backup integrity check failed for provider '{provider.Name}'", backupPath);
                ApplyOneProviderInTransaction(connection, provider, hash);
                applied.Add(provider.Name);
            }
            catch (Exception ex)
            {
                return new SchemaRunResult(false, applied, ex.Message, backupPath);
            }
        }
        PruneBackups(backupDirectory, keep: 3);
        return new SchemaRunResult(true, applied, null, null);
    }

    private static string StatementsHash(ISchemaProvider provider) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", provider.Statements)))).ToLowerInvariant();

    /// <summary>The value recorded for this provider, or null when the store has no meta table yet (a
    /// brand-new file, before Store's own provider ran) or no row for it.</summary>
    private static string? RecordedHash(SqliteConnection connection, string providerName)
    {
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key";
            command.Parameters.AddWithValue("$key", $"schema:{providerName}");
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static void ApplyOneProviderInTransaction(SqliteConnection connection, ISchemaProvider provider, string hash)
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
            RecordStep(connection, transaction, provider.Name, hash);
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
    private static void RecordStep(SqliteConnection connection, SqliteTransaction transaction, string providerName, string hash)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO meta(key, value) VALUES ($key, $value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", $"schema:{providerName}");
        command.Parameters.AddWithValue("$value", hash);
        command.ExecuteNonQuery();
    }

    private static void Backup(SqliteConnection connection, string backupPath)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path";
        command.Parameters.AddWithValue("$path", backupPath);
        command.ExecuteNonQuery();
    }

    /// <summary>Only <c>pre-*.db</c> files in <paramref name="backupDirectory"/> are candidates; the
    /// newest <paramref name="keep"/> by write time stay (a name order would sort by provider name
    /// first). A locked or read-only file never fails a successful run.</summary>
    private static void PruneBackups(string backupDirectory, int keep)
    {
        IEnumerable<string> stale = Directory.GetFiles(backupDirectory, "pre-*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(keep);
        foreach (string path in stale)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Pooling off: a pooled connection keeps the copy's file handle after Dispose, and a
    /// running server would then hold every backup open until it exits.</summary>
    private static bool IntegrityOk(string backupPath)
    {
        using SqliteConnection copy = new($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
        copy.Open();
        using SqliteCommand command = copy.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        return string.Equals(Convert.ToString(command.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase);
    }
}
