using System.Text.RegularExpressions;

namespace Grimora.Store.Data;

/// <summary>
/// The one match timeout every regex in Grimora carries (source-generated attributes pass
/// <see cref="Milliseconds"/>; the few runtime-built patterns pass <see cref="Span"/>). A pattern that
/// runs longer than this is treated as a failed match, never as a hang. A longer allowance needs its own
/// constant next to the regex, with a comment giving the reason.
///
/// The <c>...OrFalse</c> / <c>...OrKeep</c> / <c>...OrEmpty</c> / <c>...OrWhole</c> extensions are how code
/// under src/ that reads untrusted text calls a regex: a <see cref="RegexMatchTimeoutException"/> means "no
/// match for this item", the caller carries on, and nothing escapes a hook or crashes the service. (The
/// secret scrubber does not use them: it fails closed instead, see <c>SecretScrubber</c>.)
///
/// Grimora.Cli compiles this file as a linked copy (it references no Grimora project) and defines
/// Grimora_CLI_LINKED so its copy stays internal and the two assemblies never expose the same public type to a
/// project that references both.
/// </summary>
#if Grimora_CLI_LINKED
internal static class RegexTimeout
#else
public static class RegexTimeout
#endif
{
    /// <summary>The shared limit, in milliseconds, for the attribute's matchTimeoutMilliseconds parameter.</summary>
    public const int Milliseconds = 2000;

    /// <summary>The same limit as a <see cref="TimeSpan"/>, for the Regex overloads that take one.</summary>
    public static readonly TimeSpan Span = TimeSpan.FromMilliseconds(Milliseconds);

    /// <summary><c>IsMatch</c>, where a timeout reads as no match.</summary>
    public static bool IsMatchOrFalse(this Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary><c>Replace</c>, where a timeout keeps the input unchanged.</summary>
    public static string ReplaceOrKeep(this Regex regex, string input, string replacement)
    {
        try
        {
            return regex.Replace(input, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    /// <summary><c>Replace</c>, where a timeout keeps the input unchanged.</summary>
    public static string ReplaceOrKeep(this Regex regex, string input, MatchEvaluator evaluator)
    {
        try
        {
            return regex.Replace(input, evaluator);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    /// <summary><c>Match</c>, where a timeout gives <see cref="Match.Empty"/> (not a success).</summary>
    public static Match MatchOrEmpty(this Regex regex, string input)
    {
        try
        {
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return Match.Empty;
        }
    }

    /// <summary><c>Matches</c>, read in full; a timeout gives no matches at all.</summary>
    public static IReadOnlyList<Match> MatchesOrEmpty(this Regex regex, string input)
    {
        try
        {
            // ReSharper disable once RedundantEnumerableCastCall (MatchCollection enumerates as object in this target; the cast is what types it)
            return [.. regex.Matches(input).Cast<Match>()];
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    /// <summary><c>Split</c>, where a timeout gives the whole input as one piece.</summary>
    public static string[] SplitOrWhole(this Regex regex, string input)
    {
        try
        {
            return regex.Split(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return [input];
        }
    }

    /// <summary>
    /// Replaces every occurrence of <paramref name="literal"/> (matched as plain text, ignoring case) in
    /// <paramref name="input"/>; a timeout keeps the input unchanged.
    /// </summary>
    public static string ReplaceLiteralIgnoreCase(string input, string literal, string replacement)
    {
        try
        {
            return Regex.Replace(input, Regex.Escape(literal), replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout.Span);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }
}
