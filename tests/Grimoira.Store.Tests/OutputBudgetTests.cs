using Grimoira.Store.Tools;
using Xunit;

namespace Grimoira.Store.Tests;

// Copied verbatim from mcp.cs's Budget (mcp.cs:128-135); pinned here as direct behaviour assertions
// since Budget is a private implementation detail of the compiled mcp.dll and cannot be invoked as an
// oracle from outside it.
public class OutputBudgetTests
{
    [Fact]
    public void TextUnderTheCapIsReturnedUnchanged()
    {
        Assert.Equal("short answer", OutputBudget.Clip("short answer", cap: 1800));
    }

    [Fact]
    public void TextOverTheCapIsTrimmedAtALineBoundaryAndSaysSo()
    {
        string text = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"line {i}"));
        string clipped = OutputBudget.Clip(text, cap: 100);

        Assert.True(clipped.Length < text.Length);
        Assert.Contains("more line(s) trimmed", clipped);
        Assert.DoesNotContain("line 499", clipped);
    }

    [Fact]
    public void DefaultCapMatchesMcpCsOutCap()
    {
        Assert.Equal(1800, OutputBudget.DefaultCap);
    }
}
