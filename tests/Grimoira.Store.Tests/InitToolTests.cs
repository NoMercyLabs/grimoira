using Grimoira.TestSupport;
using Grimoira.Store.Tools;
using Xunit;

namespace Grimoira.Store.Tests;

public class InitToolTests
{
    [Fact]
    public void MatchesTodaysCliOutput()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("init");
        try
        {
            (string stdout, int exitCode) = GrimoiraCliRunner.Run($"init --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = GrimoiraCliRunner.InstanceDbPath(instance);
            string actual = Normalize(new InitTool().Execute(instance, dbPath) + "\n");

            Assert.Equal(expected, actual);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
