using Grimoira.Brain.Schema;
using Grimoira.Docs.Schema;
using Grimoira.Facts.Schema;
using Grimoira.Graph.Schema;
using Grimoira.Memory.Schema;
using Grimoira.Store.Data;
using Grimoira.Store.Schema;
using Microsoft.Data.Sqlite;

namespace Grimoira.Server.Data;

/// <summary>
/// One open store per project inside the server (RESTRUCTURE.md "Slice 26"): the first call for a
/// project name opens and schema-migrates its <c>grimoira.db</c> under the server's data directory; every
/// later call — from any session — reuses the same <see cref="SqliteConnection"/> through the same
/// <see cref="ProjectHandle"/>, so two sessions writing to the same project never open a second
/// connection to the same file (the "database is locked" fix).
/// </summary>
public sealed class ProjectStore(string dataDir) : IDisposable
{
    private readonly Dictionary<string, ProjectHandle> _handles = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The schema every project store is built from — the same providers
    /// <c>InitFull</c> applies, plus <see cref="BrainSchema"/> (node/ref/usage) which the brain_* tools
    /// need and <see cref="GraphIndexSchema"/> for the edges indexes.</summary>
    private static IReadOnlyList<ISchemaProvider> Providers =>
    [
        new StoreSchema(), new FactsSchema(), new MemorySchema(), new DocsSchema(),
        new GraphSchema(), new GraphIndexSchema(), new BrainSchema(),
    ];

    public ProjectHandle Acquire(string instance)
    {
        lock (_gate)
        {
            if (_handles.TryGetValue(instance, out ProjectHandle? existing)) return existing;

            string projectDir = Path.Combine(dataDir, instance);
            Directory.CreateDirectory(projectDir);
            string dbPath = Path.Combine(projectDir, "grimoira.db");
            SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunResult schema = SchemaRunner.Run(connection, Providers, Path.Combine(projectDir, "schema-backups"));
            if (!schema.Success)
            {
                connection.Dispose();
                throw new InvalidOperationException($"could not open project store '{instance}': {schema.Error}");
            }

            ProjectHandle handle = new(connection, dbPath);
            _handles[instance] = handle;
            return handle;
        }
    }

    public IReadOnlyCollection<string> OpenInstances
    {
        get { lock (_gate) return [.. _handles.Keys]; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (ProjectHandle handle in _handles.Values) handle.Connection.Dispose();
            _handles.Clear();
        }
    }
}

/// <summary>The one open connection for a project, plus the single writer gate every writing call
/// serializes through, so two sessions writing at once never race the same connection object.
/// A read-only tool call (<see cref="Grimoira.Store.Tools.ITool.IsReadOnly"/>) does not queue on the
/// gate: it runs on its own short-lived <see cref="OpenReader"/> connection, and SQLite's WAL mode
/// (<see cref="StoreConnection.ApplyPragmas"/>) lets that reader run beside the writer (issue #24).</summary>
public sealed class ProjectHandle(SqliteConnection connection, string dbPath)
{
    public SqliteConnection Connection { get; } = connection;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    internal string CallsLogPath => Path.Combine(Path.GetDirectoryName(dbPath)!, "calls.log");

    /// <summary>How long a reader's own side write (a gap log, a usage bump) waits for the writer before it
    /// is dropped; short on purpose, so a read never waits on a long index job.</summary>
    private const int ReaderBusyTimeoutMs = 3000;

    /// <summary>A second connection to the same file for one read-only call. The caller disposes it.</summary>
    public SqliteConnection OpenReader()
    {
        SqliteConnection reader = new($"Data Source={dbPath};Foreign Keys=True");
        reader.Open();
        using SqliteCommand command = reader.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={ReaderBusyTimeoutMs}";
        command.ExecuteNonQuery();
        return reader;
    }
}
