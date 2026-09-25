using Aitm.Server.Data;
using Aitm.TestSupport;
using Xunit;

namespace Aitm.Server.Tests;

// RESTRUCTURE.md slice 29b: "aitm.cs's dispatch body moves into CliDispatch.Run(string[] args, string
// cwd, TextWriter stdout, TextWriter stderr) -> int ... Every Environment.Exit(n) becomes return n."
// Each case runs one verb in-process through CliDispatch.Run and the same verb through the bin-cli
// binary, on fresh test instances, and needs stdout, stderr and the exit code to match byte for byte.
// The error cases (unknown verb, dropped verb, missing flag, bad id) return 2 and this test process is
// still running afterwards, which is the point: inside the server an Environment.Exit would stop it.
public class CliDispatchTests
{
    public static TheoryData<string, string[]> Cases() => new()
    {
        { "help", ["help"] },
        { "todos", ["todos"] },
        { "unknown-verb", ["no-such-verb"] },
        { "dropped-verb", ["loop"] },
        { "missing-flag", ["import"] },
        { "bad-id", ["done", "abc"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void RunInProcessMatchesTheBinCliBinary(string name, string[] args)
    {
        string inProcInstance = AitmCliRunner.NewTestInstance("clidispatch-" + name);
        string binInstance = AitmCliRunner.NewTestInstance("clidispatch-bin-" + name);
        try
        {
            using StringWriter stdout = new();
            using StringWriter stderr = new();
            int exit = CliDispatch.Run([.. args, "--instance", inProcInstance], Directory.GetCurrentDirectory(), stdout, stderr);

            OldVsNewCli.Result bin = OldVsNewCli.Run(OldVsNewCli.BinCliDll(), binInstance, string.Join(' ', args));

            Assert.Equal(bin.ExitCode, exit);
            Assert.Equal(bin.Stdout, stdout.ToString());
            Assert.Equal(bin.Stderr, stderr.ToString());
        }
        finally
        {
            AitmCliRunner.DeleteInstance(inProcInstance);
            AitmCliRunner.DeleteInstance(binInstance);
        }
    }

    [Fact]
    public void UnknownVerbReturnsTwoAndTheCallerKeepsRunning()
    {
        string instance = AitmCliRunner.NewTestInstance("clidispatch-alive");
        try
        {
            using StringWriter stdout = new();
            using StringWriter stderr = new();
            int exit = CliDispatch.Run(["no-such-verb", "--instance", instance], Directory.GetCurrentDirectory(), stdout, stderr);

            Assert.Equal(2, exit);
            Assert.StartsWith("error: unknown command 'no-such-verb'.", stderr.ToString());
            Assert.False(Environment.HasShutdownStarted);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
