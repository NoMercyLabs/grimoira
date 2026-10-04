using Grimoira.TestSupport;
using Xunit;

namespace Grimoira.Server.Tests;

// The snapshot oracles (mcp.dll and the slice-24 CLI) live in the user temp folder, where a clean-up can remove their dependency dlls
// while the main dll and the stamp survive (2026-10-04: 77 parity tests red, the snapshot answered nothing).
// A cached snapshot counts only when every file its stamp lists is still there.
public class SnapshotStampTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("grimoira-mcp-snapshot-cache-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SnapshotWithEveryListedFileIsComplete()
    {
        File.WriteAllText(Path.Combine(_dir, "mcp.dll"), "x");
        File.WriteAllText(Path.Combine(_dir, "Microsoft.Extensions.Hosting.dll"), "x");
        File.WriteAllText(Path.Combine(_dir, ".built-ok"), "mcp.dll\nMicrosoft.Extensions.Hosting.dll\n");

        Assert.True(SnapshotStamp.IsComplete(_dir));
    }

    [Fact]
    public void SnapshotMissingAListedFileIsNotComplete()
    {
        File.WriteAllText(Path.Combine(_dir, "mcp.dll"), "x");
        File.WriteAllText(Path.Combine(_dir, ".built-ok"), "mcp.dll\nMicrosoft.Extensions.Hosting.dll\n");

        Assert.False(SnapshotStamp.IsComplete(_dir));
    }

    [Fact]
    public void StampWithoutAFileListIsNotComplete()
    {
        File.WriteAllText(Path.Combine(_dir, "mcp.dll"), "x");
        File.WriteAllText(Path.Combine(_dir, ".built-ok"), "2026-10-01T12:39:02.8796036Z");

        Assert.False(SnapshotStamp.IsComplete(_dir));
    }

    [Fact]
    public void NoStampIsNotComplete()
    {
        File.WriteAllText(Path.Combine(_dir, "mcp.dll"), "x");

        Assert.False(SnapshotStamp.IsComplete(_dir));
    }
}
