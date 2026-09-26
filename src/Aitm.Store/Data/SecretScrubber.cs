using System.Text.RegularExpressions;

namespace Aitm.Store.Data;

/// <summary>
/// Removes token-shaped strings from text before it lands in a store (docs/RESTRUCTURE.md, design
/// checklist "Secrets in outputs"): JWTs, <c>Bearer &lt;token&gt;</c> headers, and common key prefixes
/// (GitHub, OpenAI, AWS, Slack, PEM private keys). Shared by the new <c>IndexChatTool</c> (Aitm.Memory)
/// and today's aitm.cs <c>IndexChat</c>, because aitm.cs is what runs in production today and this is a
/// security fix. Ordinary text, code and hashes (git SHAs, sha256 digests) are left alone — they carry
/// no dots-and-dashes token shape and no key prefix.
/// </summary>
public static partial class SecretScrubber
{
    // Order matters: PEM blocks and Bearer headers are consumed whole first, so a JWT sitting inside
    // either of them is counted once, under the outer kind, not twice.
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("private-key", PrivateKeyBlock()),
        ("bearer", BearerHeader()),
        ("jwt", JsonWebToken()),
        ("github", GithubToken()),
        ("openai", OpenAiKey()),
        ("aws", AwsAccessKeyId()),
        ("slack", SlackToken()),
    ];

    /// <summary>Replaces every token-shaped string with <c>[redacted:&lt;kind&gt;]</c> and returns the
    /// scrubbed text plus how many of each kind were found.</summary>
    public static (string Text, IReadOnlyDictionary<string, int> Counts) Redact(string text)
    {
        Dictionary<string, int> counts = [];
        string result = text;
        foreach ((string kind, Regex pattern) in Patterns)
        {
            result = pattern.Replace(result, _ =>
            {
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                return $"[redacted:{kind}]";
            });
        }
        return (result, counts);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PrivateKeyBlock();
    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-_.+/=]{10,}", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex BearerHeader();
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}(?![A-Za-z0-9_-])", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex JsonWebToken();
    [GeneratedRegex(@"\b(?:ghp_|gho_|github_pat_)[A-Za-z0-9_]{20,}\b", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex GithubToken();
    [GeneratedRegex(@"\bsk-[A-Za-z0-9]{20,}\b", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex OpenAiKey();
    [GeneratedRegex(@"\bAKIA[A-Z0-9]{12,}\b", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex AwsAccessKeyId();
    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex SlackToken();
}
