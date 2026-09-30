using System.Text.RegularExpressions;
using Grimora.Server.Data;
using Grimora.Store.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Layout.Tests;

// Test classes in this assembly run in parallel, and every parity test releases its store with
// SqliteConnection.ClearAllPools() (GrimoraCliRunner.DeleteInstance), which is process-wide. Microsoft.Data.Sqlite
// 10.0.9 marks a pooled connection active before it records its owner (dotnet/efcore#39008, fixed upstream in
// #39012 but in no published package up to 10.0.12), so a clear that lands in that window reclaims the connection
// as "leaked" and disposes its sqlite3 handle under the thread that is still opening it:
// `ObjectDisposedException: SQLitePCL.sqlite3` out of SqliteConnection.Open(), seen once in 20 suite runs from
// StoreFactsCliParityTests through CliDispatch. A connection opened with Pooling=False never enters a pool, so a
// clear cannot touch it. Therefore every connection this assembly opens in-process, including the one the CLI
// dispatcher opens for itself, is unpooled.
public sealed partial class SqlitePoolClearDoesNotDisposeAnOpeningConnectionTests
{
    [Fact]
    public async Task CliDispatchSurvivesAProcessWidePoolClearOnAnotherThread()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "grimora-poolrace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            using CancellationTokenSource stop = new();
            Task clearer = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested) SqliteConnection.ClearAllPools();
            });

            // Four dispatchers on their own instances, three seconds: the window is a few instructions wide, so
            // one pass is not enough to see it, and a few thousand passes are.
            DateTime until = DateTime.UtcNow.AddSeconds(3);
            Task[] openers = [.. Enumerable.Range(0, 4).Select(n => Task.Run(() =>
            {
                while (DateTime.UtcNow < until)
                {
                    using StringWriter stdout = new();
                    using StringWriter stderr = new();
                    CliDispatch.Run(["stats", "--instance", $"race{n}"], dataDir, stdout, stderr, dataDir);
                }
            }))];
            Exception? failure = await Record.ExceptionAsync(() => Task.WhenAll(openers));

            stop.Cancel();
            await clearer;
            Assert.Null(failure);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDir, true);
        }
    }

    [Fact]
    public void EveryInProcessConnectionInThisAssemblyIsUnpooled()
    {
        string[] files =
        [
            .. Directory.GetFiles(Path.Combine(RepoPaths.Root, "tests", "Grimora.Layout.Tests"), "*.cs"),
            Path.Combine(RepoPaths.Root, "src", "Grimora.Server", "Data", "CliDispatch.cs"),
        ];
        List<string> pooled = [];
        foreach (string file in files)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (DataSource().IsMatch(lines[i]) && !lines[i].Contains("Pooling=False", StringComparison.Ordinal))
                    pooled.Add($"{Path.GetRelativePath(RepoPaths.Root, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(pooled.Count == 0,
            "Add Pooling=False: a pooled open in this process can be disposed by a concurrent ClearAllPools()\n" +
            string.Join('\n', pooled));
    }

    // An interpolated connection string with a path, which is how every open here is written; not this pattern.
    [GeneratedRegex(@"\$""Data Source=\{", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex DataSource();
}
