using System.Diagnostics;
using System.Text.Json;
using Aitm.Cli.Tools;
using Xunit;

namespace Aitm.Cli.Tests;

/// <summary>
/// RESTRUCTURE.md slice 28: `.mcp.json`'s headersHelper runs `aitm server headers`, "a C# verb printing
/// {"Authorization":"Bearer &lt;token&gt;"}" from the server token file (`server.token` in the AITM
/// data dir, the path Aitm.Server's ServerToken uses). A missing token prints `{}` and exits 0 so Claude
/// Code falls back cleanly.
/// </summary>
public sealed class ServerHeadersCommandTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("aitm-headers-").FullName;

    [Fact]
    public void PrintsTheBearerHeaderFromTheServerTokenFile()
    {
        File.WriteAllText(Path.Combine(_dataDir, "server.token"), "abc123\n");
        using StringWriter stdout = new();

        int exit = ServerHeadersCommand.Run(_dataDir, stdout);

        Assert.Equal(0, exit);
        Assert.Equal("""{"Authorization":"Bearer abc123"}""", stdout.ToString().Trim());
        using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("Bearer abc123", doc.RootElement.GetProperty("Authorization").GetString());
    }

    [Fact]
    public void MissingTokenPrintsAnEmptyObjectAndExitsZero()
    {
        using StringWriter stdout = new();

        int exit = ServerHeadersCommand.Run(_dataDir, stdout);

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout.ToString().Trim());
    }

    [Fact]
    public void EmptyTokenFilePrintsAnEmptyObject()
    {
        File.WriteAllText(Path.Combine(_dataDir, "server.token"), "  \n");
        using StringWriter stdout = new();

        Assert.Equal(0, ServerHeadersCommand.Run(_dataDir, stdout));
        Assert.Equal("{}", stdout.ToString().Trim());
    }

    [Fact]
    public void MissingDataDirPrintsAnEmptyObject()
    {
        using StringWriter stdout = new();

        Assert.Equal(0, ServerHeadersCommand.Run(Path.Combine(_dataDir, "nope"), stdout));
        Assert.Equal("{}", stdout.ToString().Trim());
    }

    [Fact]
    public void TheBuiltVerbReadsAitmDataDirAndPrintsTheTokenOnlyOnStdout()
    {
        File.WriteAllText(Path.Combine(_dataDir, "server.token"), "tok-from-exe");

        (string stdout, string stderr, int exit) = RunBuiltCli(["server", "headers"], _dataDir);

        Assert.Equal(0, exit);
        Assert.Equal("""{"Authorization":"Bearer tok-from-exe"}""", stdout.Trim());
        Assert.DoesNotContain("tok-from-exe", stderr);
    }

    internal static (string Stdout, string Stderr, int Exit) RunBuiltCli(string[] args, string dataDir, IDictionary<string, string>? extraEnv = null)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "Aitm.Cli.dll");
        ProcessStartInfo psi = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["AITM_DATA_DIR"] = dataDir;
        if (extraEnv is not null) foreach ((string k, string v) in extraEnv) psi.Environment[k] = v;
        using Process p = Process.Start(psi)!;
        p.StandardInput.Write("{}");
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("aitm did not exit within 30 s");
        }
        return (stdout.Result, stderr.Result, p.ExitCode);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }
}
