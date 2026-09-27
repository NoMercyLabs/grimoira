using System.Text;
using Aitm.Server.Data;
using Aitm.Store.Data;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aitm.Server.Tests;

// A verb cannot be cancelled mid-statement: /cli answers exit 124 on its timeout while the verb keeps running on
// its own thread and holds the project gate. That orphan counts as a call in flight, so the idle exit and the
// shutdown drain wait for it and the store is never disposed under it.
public sealed class CliVerbOutlivingItsTimeoutTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-orphan-verb-").FullName;
    private readonly string _projectDir = Path.Combine(Path.GetTempPath(), $"test-orphan-{Guid.NewGuid():N}");
    private readonly ManualResetEventSlim _release = new(false);

    public void Dispose()
    {
        _release.Set();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private DefaultHttpContext Request()
    {
        return new DefaultHttpContext
        {
            Request =
            {
                Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"args\":[\"slow\"],\"cwd\":" + System.Text.Json.JsonSerializer.Serialize(_projectDir) + "}")),
                Headers = { [RequestProjectResolver.ProjectDirHeader] = _projectDir },
            },
        };
    }

    private int BlockingVerb(string[] args, string cwd, TextWriter stdout, TextWriter stderr, string instance, string dataDir, Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        _release.Wait(TimeSpan.FromSeconds(30));
        return 0;
    }

    [Fact]
    public async Task TheOrphanVerbKeepsTheCallInFlightAndBlocksTheIdleExitUntilItEnds()
    {
        using ProjectStore store = new(_dataDir);
        int stops = 0;
        IdleExit idle = new(TimeSpan.FromMilliseconds(1), () => stops++);

        await CliEndpoint.Handle(Request(), store, _dataDir, idle, BlockingVerb, TimeSpan.FromMilliseconds(300));
        Thread.Sleep(50);

        Assert.Equal(1, idle.InFlight);
        Assert.False(idle.CheckAndStopIfIdle());
        Assert.Equal(0, stops);

        _release.Set();
        Assert.True(idle.WaitForDrain(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, idle.InFlight);
    }

    [Fact]
    public async Task TheShutdownDrainWaitsForTheOrphanVerbBeforeTheStoreMayBeDisposed()
    {
        using ProjectStore store = new(_dataDir);
        IdleExit idle = new(TimeSpan.FromMinutes(30), () => { });

        await CliEndpoint.Handle(Request(), store, _dataDir, idle, BlockingVerb, TimeSpan.FromMilliseconds(300));

        Assert.False(idle.WaitForDrain(TimeSpan.FromMilliseconds(300)));
        _release.Set();
        Assert.True(idle.WaitForDrain(TimeSpan.FromSeconds(10)));
        string instance = StoreConnection.ResolveInstance(null, _projectDir, Directory.GetCurrentDirectory());
        Assert.True(store.Acquire(instance).Gate.Wait(TimeSpan.FromSeconds(5)), "the orphan verb never released the project gate");
    }
}
