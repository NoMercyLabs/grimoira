namespace Aitm.Store.Data;

/// <summary>
/// The one match timeout every regex in AITM carries (source-generated attributes pass
/// <see cref="Milliseconds"/>; the few runtime-built patterns pass <see cref="Span"/>). A pattern that
/// runs longer than this is treated as a failed match, never as a hang. A longer allowance needs its own
/// constant next to the regex, with a comment giving the reason. Aitm.Cli compiles this file as a linked
/// copy (it references no AITM project) and defines AITM_CLI_LINKED so its copy stays internal and the
/// two assemblies never expose the same public type to a project that references both.
/// </summary>
#if AITM_CLI_LINKED
internal static class RegexTimeout
#else
public static class RegexTimeout
#endif
{
    /// <summary>The shared limit, in milliseconds, for the attribute's matchTimeoutMilliseconds parameter.</summary>
    public const int Milliseconds = 2000;

    /// <summary>The same limit as a <see cref="TimeSpan"/>, for the Regex overloads that take one.</summary>
    public static readonly TimeSpan Span = TimeSpan.FromMilliseconds(Milliseconds);
}
