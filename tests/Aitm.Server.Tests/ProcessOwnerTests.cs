using System.Runtime.InteropServices;
using System.Text.Json;
using Aitm.Server.Data;
using Xunit;

namespace Aitm.Server.Tests;

// Ported from process-owner.test.mjs. Only ever starts and stops processes it spawns itself; never
// adopts or inspects a real, pre-existing PID.
public class ProcessOwnerTests
{
    private static string TempRoot() => Directory.CreateTempSubdirectory("aitm-owner-").FullName;

    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static (string Command, string[] Args) ExitImmediately() =>
        IsWindows ? ("cmd.exe", ["/c", "exit 0"]) : ("/bin/sh", ["-c", "exit 0"]);
    private static (string Command, string[] Args) SleepLong() =>
        IsWindows
            ? ("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"])
            : ("/bin/sh", ["-c", "sleep 30"]);

    [Fact]
    public void PilotExcludesSiblingWorkspaces()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "NoMercy"));
            Directory.CreateDirectory(Path.Combine(root, "NoMercy-other"));

            Assert.NotNull(ProcessOwner.PilotRoot(Path.Combine(root, "NoMercy"), Path.Combine(root, "NoMercy")));
            Assert.Null(ProcessOwner.PilotRoot(Path.Combine(root, "NoMercy-other"), Path.Combine(root, "NoMercy")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordsNormalChildExit()
    {
        string root = TempRoot();
        try
        {
            (string cmd, string[] args) = ExitImmediately();
            OwnedProcess run = ProcessOwner.LaunchOwned(cmd, args, root, root);
            await run.Process.WaitForExitAsync();
            await run.Recorded.WaitAsync(TimeSpan.FromSeconds(10));

            OwnerRecord record = JsonSerializer.Deserialize<OwnerRecord>(await File.ReadAllTextAsync(run.FileName))!;
            Assert.Equal("exited", record.State);
            Assert.Equal(0, record.ExitCode);
            Assert.NotNull(record.ChildPid);
            Assert.Equal(Environment.ProcessId, record.Owner.Pid);
            Assert.False(run.Cancel());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelsOnlyTheOriginalLiveChild()
    {
        string root = TempRoot();
        OwnedProcess? run = null;
        try
        {
            (string cmd, string[] args) = SleepLong();
            run = ProcessOwner.LaunchOwned(cmd, args, root, root);
            Assert.True(run.Cancel());
            await run.Process.WaitForExitAsync();
            await run.Recorded.WaitAsync(TimeSpan.FromSeconds(10));

            OwnerRecord record = JsonSerializer.Deserialize<OwnerRecord>(await File.ReadAllTextAsync(run.FileName))!;
            Assert.Equal("exited", record.State);
        }
        finally
        {
            run?.Cancel();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelingOneOwnerLeavesAnotherLiveChildAlone()
    {
        string root = TempRoot();
        (string cmd, string[] args) = SleepLong();
        OwnedProcess first = ProcessOwner.LaunchOwned(cmd, args, root, root);
        OwnedProcess second = ProcessOwner.LaunchOwned(cmd, args, root, root);
        try
        {
            Assert.True(first.Cancel());
            await first.Process.WaitForExitAsync();
            await first.Recorded.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(second.Process.HasExited);
            OwnerRecord secondRecord = JsonSerializer.Deserialize<OwnerRecord>(await File.ReadAllTextAsync(second.FileName))!;
            Assert.Equal("running", secondRecord.State);

            Assert.True(second.Cancel());
            await second.Process.WaitForExitAsync();
            await second.Recorded.WaitAsync(TimeSpan.FromSeconds(10));

            secondRecord = JsonSerializer.Deserialize<OwnerRecord>(await File.ReadAllTextAsync(second.FileName))!;
            Assert.Equal("exited", secondRecord.State);
        }
        finally
        {
            first.Cancel();
            second.Cancel();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AChildThatExitsAtOnceIsStillRecordedAsExited()
    {
        // The Exited handler must be attached before Start: a child that exits before the handler
        // is attached never raises it, and its record says "running" forever (CI run 36170373543).
        string root = TempRoot();
        try
        {
            (string cmd, string[] args) = ExitImmediately();
            List<OwnedProcess> runs = [];
            for (int i = 0; i < 40; i++) runs.Add(ProcessOwner.LaunchOwned(cmd, args, root, root));
            int missed = 0;
            foreach (OwnedProcess run in runs)
            {
                await run.Process.WaitForExitAsync();
                try { await run.Recorded.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { missed++; }
            }
            Assert.Equal(0, missed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedSpawnIsRecorded()
    {
        string root = TempRoot();
        try
        {
            OwnedProcess run = ProcessOwner.LaunchOwned(Path.Combine(root, "does-not-exist.exe"), [], root, root);

            OwnerRecord record = JsonSerializer.Deserialize<OwnerRecord>(File.ReadAllText(run.FileName))!;
            Assert.Equal("failed", record.State);
            Assert.False(run.Cancel());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RetentionRemovesOldTerminalRecordsButPreservesUnresolvedOwnership()
    {
        string root = TempRoot();
        try
        {
            string directory = Path.Combine(root, ".claude", "scratch", "process-owners");
            Directory.CreateDirectory(directory);
            string[] paths =
            [
                Path.Combine(directory, $"{"1".PadLeft(36, '0')}.json"),
                Path.Combine(directory, $"{"2".PadLeft(36, '0')}.json"),
                Path.Combine(directory, $"{"3".PadLeft(36, '0')}.json"),
            ];
            File.WriteAllText(paths[0], """{"version":1,"state":"exited","ended_at":"2026-01-01"}""");
            File.WriteAllText(paths[1], """{"version":1,"state":"failed","ended_at":"2026-01-02"}""");
            File.WriteAllText(paths[2], """{"version":1,"state":"running"}""");

            ProcessOwner.TrimCompleted(root, keep: 1);

            Assert.False(File.Exists(paths[0]));
            Assert.True(File.Exists(paths[1]));
            Assert.True(File.Exists(paths[2]));

            (bool limited, _) = ProcessOwner.InspectOwners(root, limit: 1);
            Assert.True(limited);
            (_, List<Dictionary<string, object?>> records) = ProcessOwner.InspectOwners(root);
            Assert.Contains("not verified", (string)records[0]["liveness"]!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
