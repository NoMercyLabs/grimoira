using Aitm.Store.Tests.Support;
using Aitm.Store.Tools;
using Xunit;

namespace Aitm.Store.Tests;

public class InitToolTests
{
    [Fact]
    public void MatchesTodaysCliOutput()
    {
        string instance = AitmCliRunner.NewTestInstance("init");
        try
        {
            (string stdout, int exitCode) = AitmCliRunner.Run($"init --instance {instance}");
            Assert.Equal(0, exitCode);
            string expected = Normalize(stdout);

            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            string actual = Normalize(new InitTool().Execute(instance, dbPath) + "\n");

            Assert.Equal(expected, actual);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
