using Aitm.Store.Data;
using Xunit;

namespace Aitm.Store.Tests;

// RESTRUCTURE.md docs/RESTRUCTURE.md, design checklist "Secrets in outputs": "index-chat removes
// token-shaped strings (JWTs, `Bearer ...`, common key prefixes) before it stores chat." This is the
// shared scrubber both the new IndexChatTool and today's aitm.cs IndexChat call.
public class SecretScrubberTests
{
    [Fact]
    public void RedactsAJwt()
    {
        string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        (string text, IReadOnlyDictionary<string, int> counts) = SecretScrubber.Redact($"token: {jwt} end");

        Assert.DoesNotContain(jwt, text);
        Assert.Contains("[redacted:jwt]", text);
        Assert.Equal(1, counts["jwt"]);
    }

    [Fact]
    public void RedactsABearerHeader()
    {
        string header = "Bearer abcDEF123456.ghIJKL7890-secretvalue";
        (string text, IReadOnlyDictionary<string, int> counts) = SecretScrubber.Redact($"Authorization: {header}");

        Assert.DoesNotContain(header, text);
        Assert.Contains("[redacted:bearer]", text);
        Assert.Equal(1, counts["bearer"]);
    }

    [Theory]
    [InlineData("ghp_1234567890abcdefghijklmnopqrstuvwx", "github")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOP", "github")]
    [InlineData("gho_1234567890abcdefghijklmnopqrstuvwx", "github")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyzABCDEFGHIJ", "openai")]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "aws")]
    [InlineData("xoxb-1234567890-1234567890123-abcdefghijklmnopqrstuvwx", "slack")]
    public void RedactsCommonKeyPrefixes(string secret, string kind)
    {
        (string text, IReadOnlyDictionary<string, int> counts) = SecretScrubber.Redact($"key={secret};");

        Assert.DoesNotContain(secret, text);
        Assert.Contains($"[redacted:{kind}]", text);
        Assert.Equal(1, counts[kind]);
    }

    [Fact]
    public void RedactsAPemPrivateKeyBlock()
    {
        string pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIBOgIBAAJBAK8example\n-----END RSA PRIVATE KEY-----";
        (string text, IReadOnlyDictionary<string, int> counts) = SecretScrubber.Redact($"key file:\n{pem}\ndone");

        Assert.DoesNotContain("MIIBOgIBAAJBAK8example", text);
        Assert.Contains("[redacted:private-key]", text);
        Assert.Equal(1, counts["private-key"]);
    }

    [Fact]
    public void DoesNotRedactOrdinaryTextCodeOrHashes()
    {
        string plain = "See docs at v1.2.3-beta and commit 4cdd17b3d43f2a1b5c6d7e8f9a0b1c2d3e4f5061 "
            + "(sha256 e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855). "
            + "The function isUnauthorized() returns 401 or 403, and const skValue = 'not-a-secret';";

        (string text, IReadOnlyDictionary<string, int> counts) = SecretScrubber.Redact(plain);

        Assert.Equal(plain, text);
        Assert.Empty(counts);
    }
}
