using Grimora.TestSupport;
using Grimora.Store.Tools;
using Xunit;

namespace Grimora.Store.Tests;

public class InitToolTests
{
    [Fact]
    public void MatchesTodaysCliOutput()
    {
        string instance = GrimoraCliRunner.NewTestInstance("init");
        try
        {
            (string stdout, int exitCode) = GrimoraCliRunner.Run($"init --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoraCliRunner.InstanceDbPath(instance);
            string actual = Normalize(new InitTool().Execute(instance, dbPath) + "\n");

            Assert.Equal(expected, actual);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
