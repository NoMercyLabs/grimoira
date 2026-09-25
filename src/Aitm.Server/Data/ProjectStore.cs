using Aitm.Brain.Schema;
using Aitm.Docs.Schema;
using Aitm.Facts.Schema;
using Aitm.Graph.Schema;
using Aitm.Memory.Schema;
using Aitm.Store.Data;
using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;

namespace Aitm.Server.Data;

/// <summary>
/// One open store per project inside the server (RESTRUCTURE.md "Slice 26"): the first call for a
/// project name opens and schema-migrates its <c>aitm.db</c> under the server's data directory; every
/// later call — from any session — reuses the same <see cref="SqliteConnection"/> through the same
/// <see cref="ProjectHandle"/>, so two sessions writing to the same project never open a second
/// connection to the same file (the "database is locked" fix).
/// </summary>
public sealed class ProjectStore : IDisposable
{
    private readonly string _dataDir;
    private readonly Dictionary<string, ProjectHandle> _handles = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ProjectStore(string dataDir) => _dataDir = dataDir;

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

            string projectDir = Path.Combine(_dataDir, instance);
            Directory.CreateDirectory(projectDir);
            string dbPath = Path.Combine(projectDir, "aitm.db");
            SqliteConnection connection = StoreConnection.Open(dbPath);
            SchemaRunResult schema = SchemaRunner.Run(connection, Providers, Path.Combine(projectDir, "schema-backups"));
            if (!schema.Success)
            {
                connection.Dispose();
                throw new InvalidOperationException($"could not open project store '{instance}': {schema.Error}");
            }

            ProjectHandle handle = new(connection);
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

/// <summary>The one open connection for a project, plus the single writer gate every tool call —
/// read or write — serialises through, so two sessions calling at once never race the same
/// connection object.</summary>
public sealed class ProjectHandle(SqliteConnection connection)
{
    public SqliteConnection Connection { get; } = connection;
    public SemaphoreSlim Gate { get; } = new(1, 1);
}
