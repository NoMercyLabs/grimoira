using System.Diagnostics;
using System.Text.RegularExpressions;
using Aitm.TestSupport;
using Xunit;

namespace Aitm.Layout.Tests;

// RESTRUCTURE.md slice 29, part 1: "Adds: `dotnet publish ...` in the build." Part 1 publishes today's
// aitm.cs dispatcher to bin-cli-next/, beside bin-cli/ and never over it: NoMercy's brain-sweep
// (section 5, caller 5) runs bin-cli/aitm.exe today and must keep working. These tests pin that the
// build publishes there, and that the published exe answers exactly as `dotnet bin-cli/aitm.dll` does
// (stdout, stderr and exit code) for a no-arg run, `help`, an unknown verb and one read verb.
public class PublishedCliMatchesBinCliTests
{
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "aitm.exe" : "aitm";

    [Fact]
    public void BuildCliScriptPublishesToBinCliNextBesideBinCli()
    {
        string script = File.ReadAllText(Path.Combine(RepoPaths.Root, "build-cli.ps1"));
        Assert.Matches(new Regex(@"dotnet publish ""\$PSScriptRoot/aitm\.cs"" .*-o ""\$PSScriptRoot/bin-cli-next"""), script);
        // bin-cli/ keeps its own build line; the publish must not replace it.
        Assert.Contains(@"-o ""$PSScriptRoot/bin-cli""", script);
    }

    [Fact]
    public void GitIgnoresTheBinCliNextOutput()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoPaths.Root, ".gitignore"));
        Assert.Contains("bin-cli-next/", lines);
    }

    public static IEnumerable<object[]> Scenarios()
    {
        (string name, string[] setup, string command)[] cases =
        [
            ("no-arg", [], ""),
            ("help", [], "help"),
            ("unknown-verb", [], "no-such-verb"),
            ("query-hit", ["init", "add --term publish-term --value published-value --category manual"], "query publish-term"),
        ];
        return cases.Select(c => new object[] { c.name, c.setup, c.command });
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void PublishedExeAnswersLikeBinCliDll(string name, string[] setup, string command)
    {
        _ = name;
        string exe = PublishedExe();
        string dll = OldVsNewCli.BinCliDll();
        string oldInstance = AitmCliRunner.NewTestInstance("s29o");
        string newInstance = AitmCliRunner.NewTestInstance("s29n");
        try
        {
            foreach (string s in setup)
            {
                RunProcess("dotnet", $"\"{dll}\" {s}", oldInstance);
                RunProcess(exe, s, newInstance);
            }
            OldVsNewCli.Result oldResult = RunProcess("dotnet", $"\"{dll}\" {command}", oldInstance);
            OldVsNewCli.Result newResult = RunProcess(exe, command, newInstance);

            Assert.Equal(Normalize(oldResult.Stdout, oldInstance), Normalize(newResult.Stdout, newInstance));
            Assert.Equal(Normalize(oldResult.Stderr, oldInstance), Normalize(newResult.Stderr, newInstance));
            Assert.Equal(oldResult.ExitCode, newResult.ExitCode);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    // query prints its own elapsed time; the two instance names differ by design.
    private static string Normalize(string s, string instance) =>
        Regex.Replace(s.Replace(instance, "<instance>"), @"\(\d+[.,]\d+ms\)", "(<ms>)");

    // verify.ps1 runs build-cli.ps1 before `dotnet test`, so locally the exe must already be there; a
    // missing exe is the failure this test exists to catch. CI builds bin-cli with its own command and
    // never runs build-cli.ps1, so there (CI=true) the test publishes a private copy the same way.
    private static string PublishedExe()
    {
        string exe = Path.Combine(RepoPaths.Root, "bin-cli-next", ExeName);
        if (File.Exists(exe)) return exe;
        if (Environment.GetEnvironmentVariable("CI") != "true")
            throw new InvalidOperationException($"{exe} not found: run build-cli.ps1 first");
        return PublishPrivateCopy();
    }

    private static readonly Lazy<string> PrivateCopy = new(() =>
    {
        string outDir = Path.Combine(Path.GetTempPath(), "aitm-slice29-publish-" + Guid.NewGuid().ToString("N"));
        OldVsNewCli.Result r = RunProcess("dotnet",
            $"publish \"{Path.Combine(RepoPaths.Root, "aitm.cs")}\" -c Release -o \"{outDir}\" -p:PublishAot=false");
        if (r.ExitCode != 0) throw new InvalidOperationException($"publish failed:\n{r.Stdout}\n{r.Stderr}");
        return Path.Combine(outDir, ExeName);
    });

    private static string PublishPrivateCopy() => PrivateCopy.Value;

    // The instance goes in AITM_INSTANCE, not --instance, so the no-arg case really has no argument.
    private static OldVsNewCli.Result RunProcess(string exe, string arguments, string? instance = null)
    {
        ProcessStartInfo psi = new(exe, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (instance is not null) psi.Environment["AITM_INSTANCE"] = instance;
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new OldVsNewCli.Result(stdout, stderr, process.ExitCode);
    }
}
