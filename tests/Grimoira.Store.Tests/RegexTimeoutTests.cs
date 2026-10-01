using System.Text.RegularExpressions;
using Grimoira.Store.Data;
using Xunit;

namespace Grimoira.Store.Tests;

// A regex that times out on untrusted text must read as "no match" for that one item, never as an
// exception that escapes a hook or crashes the service (RegexTimeout's OrFalse / OrKeep / OrEmpty helpers).
public partial class RegexTimeoutTests
{
    // Overlapping alternation under a nested quantifier: exponential on 60 a's followed by a character that
    // cannot match, so it always outruns the shared timeout.
    [GeneratedRegex("^(a|aa)+$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex OverlappingAlternationThatNeverFinishes();

    private static readonly string StuckInput = string.Concat(Enumerable.Repeat('a', 60)) + "!";

    [Fact]
    public void IsMatchOrFalseReadsATimeoutAsNoMatch()
    {
        Assert.False(OverlappingAlternationThatNeverFinishes().IsMatchOrFalse(StuckInput));
    }

    [Fact]
    public void ReplaceOrKeepReturnsTheInputUnchangedOnATimeout()
    {
        Assert.Equal(StuckInput, OverlappingAlternationThatNeverFinishes().ReplaceOrKeep(StuckInput, "x"));
    }

    [Fact]
    public void MatchOrEmptyReturnsANonMatchOnATimeout()
    {
        Assert.False(OverlappingAlternationThatNeverFinishes().MatchOrEmpty(StuckInput).Success);
    }

    [Fact]
    public void MatchesOrEmptyReturnsNothingOnATimeout()
    {
        Assert.Empty(OverlappingAlternationThatNeverFinishes().MatchesOrEmpty(StuckInput));
    }

    [Fact]
    public void SplitOrWholeReturnsTheWholeInputOnATimeout()
    {
        Assert.Equal([StuckInput], OverlappingAlternationThatNeverFinishes().SplitOrWhole(StuckInput));
    }

    [Fact]
    public void TheHelpersBehaveLikeTheRegexMethodsWhenNothingTimesOut()
    {
        Regex digits = DigitRun();
        Assert.True(digits.IsMatchOrFalse("a12"));
        Assert.Equal("a#", digits.ReplaceOrKeep("a12", "#"));
        Assert.Equal("12", digits.MatchOrEmpty("a12").Value);
        Assert.Equal(["1", "22"], digits.MatchesOrEmpty("1-22").Select(m => m.Value));
        Assert.Equal(["a", "b"], digits.SplitOrWhole("a1b"));
        Assert.Equal("a-b", RegexTimeout.ReplaceLiteralIgnoreCase("aXb", "x", "-"));
    }

    [GeneratedRegex(@"\d+", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex DigitRun();
}
