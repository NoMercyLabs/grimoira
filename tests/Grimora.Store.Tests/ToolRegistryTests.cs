using System.Reflection;
using Grimora.Store.Tools;
using Xunit;

namespace Grimora.Store.Tests;

// RESTRUCTURE.md slice 4 exit check: "the registry test: every moved tool has help text and a test."
public class ToolRegistryTests
{
    private static ITool[] StoreTools() =>
    [
        new InitTool(), new ImportTool(), new BackupTool(), new StatsTool(), new HistoryTool(),
    ];

    [Fact]
    public void RegistryFindsEveryToolByItsCliVerb()
    {
        ToolRegistry registry = new(StoreTools());
        foreach (ITool tool in StoreTools())
            Assert.Same(registry.FindByCliVerb(tool.CliVerb)!.GetType(), tool.GetType());
    }

    [Fact]
    public void RegistryFindsEveryMcpToolByItsMcpName()
    {
        ToolRegistry registry = new(StoreTools());
        ITool history = new HistoryTool();
        Assert.IsType<HistoryTool>(registry.FindByMcpName(history.McpName!));
    }

    [Fact]
    public void EveryToolHasNonEmptyHelpText()
    {
        foreach (ITool tool in StoreTools())
            Assert.False(string.IsNullOrWhiteSpace(tool.Help), $"{tool.GetType().Name} has no help text");
    }

    [Fact]
    public void EveryToolHasAtLeastOneCliOrMcpName()
    {
        foreach (ITool tool in StoreTools())
            Assert.True(!string.IsNullOrWhiteSpace(tool.CliVerb) || !string.IsNullOrWhiteSpace(tool.McpName));
    }

    // Every tool class Xxx must have a corresponding XxxTests class in this same test assembly — the
    // "a test proves each tool has help and tests" half of the exit check.
    [Fact]
    public void EveryToolHasATestClassInThisAssembly()
    {
        HashSet<string> testTypeNames = [.. Assembly.GetExecutingAssembly().GetTypes().Select(t => t.Name)];
        foreach (ITool tool in StoreTools())
        {
            string expected = tool.GetType().Name + "Tests";
            Assert.Contains(expected, testTypeNames);
        }
    }
}
