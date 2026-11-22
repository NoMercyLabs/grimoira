using Grimoira.Server.Data;
using Grimoira.TestSupport;
using Xunit;

namespace Grimoira.Server.Tests;

// AUD-9505 (issue #12): a verb that did nothing because its spine file is missing must not exit 0.
public class MissingSpineFileExitCodeTests
{
    public static TheoryData<string, string[]> Cases() => new()
    {
        { "seed-edges", ["seed-edges", "--from"] },
        { "spine-import", ["spine-import", "--from"] },
        { "brain-seed", ["brain", "seed", "--from"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MissingSpineFileReturnsNonZero(string name, string[] args)
    {
        string instance = GrimoiraCliRunner.NewTestInstance("missing-spine-" + name);
        string dataDir = Path.Combine(Path.GetTempPath(), "grimoira-missing-spine-" + Guid.NewGuid().ToString("N"));
        string missing = Path.Combine(dataDir, "no-such-spine.json");
        try
        {
            using StringWriter stdout = new();
            using StringWriter stderr = new();
            int exit = CliDispatch.Run([.. args, missing, "--instance", instance], Directory.GetCurrentDirectory(), stdout, stderr, dataDir);

            Assert.Contains("no spine file at", stderr.ToString());
            Assert.NotEqual(0, exit);
        }
        finally
        {
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }
}
