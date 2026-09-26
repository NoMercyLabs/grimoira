using System.Text.RegularExpressions;
using Aitm.Store.Data;
using Xunit;

namespace Aitm.Layout.Tests;

// Every regex in src/ and tests/ is source-generated (SYSLIB1045 is an error in .editorconfig), and the
// generated ones follow four more rules that the compiler cannot check: a name that says what the
// pattern matches, no machine-culture literal, no dead RegexOptions.Compiled, and one shared match
// timeout (Aitm.Store.Data.RegexTimeout). These tests scan the source text, so a violation fails the build
// pipeline the same way a style error does. A timeout in a scan below is a test failure on purpose:
// nothing catches RegexMatchTimeoutException here.
public partial class RegexRulesTests
{
    private const string AutomaticPrefix = "My" + "Regex";

    private sealed record Generated(string File, string Attribute, string Method);

    private static IEnumerable<string> SourceFiles()
    {
        string self = Path.GetFileName(typeof(RegexRulesTests).Name + ".cs");
        foreach (string top in new[] { "src", "tests" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, top), "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(RepoPaths.Root, file).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }
                if (Path.GetFileName(file) == self)
                {
                    continue;
                }
                yield return file;
            }
        }
    }

    private static List<Generated> AllGenerated()
    {
        List<Generated> all = [];
        foreach (string file in SourceFiles())
        {
            string text = File.ReadAllText(file);
            foreach (Match m in GeneratedRegexAttribute().Matches(text))
            {
                all.Add(new Generated(Path.GetRelativePath(RepoPaths.Root, file).Replace('\\', '/'), m.Groups["args"].Value, m.Groups["method"].Value));
            }
        }
        return all;
    }

    // The attribute's pattern literal is skipped (verbatim "" or escaped \"), leaving only the options,
    // the timeout and the culture in Args.
    [GeneratedRegex("""
        \[(?:System\.Text\.RegularExpressions\.)?GeneratedRegex\(\s*(?:@"(?:[^"]|"")*"|"(?:[^"\\]|\\.)*")(?<args>[^\]]*)\)\]\s*private static partial (?:System\.Text\.RegularExpressions\.)?Regex (?<method>\w+)\(\)
        """)]
    private static partial Regex GeneratedRegexAttribute();

    [Fact]
    public void ThereAreGeneratedRegexesToCheck()
    {
        Assert.NotEmpty(AllGenerated());
    }

    [Fact]
    public void NoRegexKeepsAnAutomaticName()
    {
        List<string> offenders = [];
        foreach (string file in SourceFiles())
        {
            if (File.ReadAllText(file).Contains(AutomaticPrefix, StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(RepoPaths.Root, file));
            }
        }
        Assert.True(offenders.Count == 0, "automatic regex names left in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoGeneratedRegexNameEndsInRegexRxOrRe()
    {
        List<string> offenders = [.. AllGenerated().Where(g => g.Method.EndsWith("Regex", StringComparison.Ordinal) || g.Method.EndsWith("Rx", StringComparison.Ordinal) || g.Method.EndsWith("Re", StringComparison.Ordinal)).Select(g => $"{g.File}: {g.Method}")];
        Assert.True(offenders.Count == 0, "generic suffix on: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoGeneratedRegexCarriesACultureString()
    {
        List<string> offenders = [.. AllGenerated().Where(g => g.Attribute.Contains('"')).Select(g => $"{g.File}: {g.Method}")];
        Assert.True(offenders.Count == 0, "culture literal on: " + string.Join(", ", offenders));
    }

    [Fact]
    public void GeneratedRegexIgnoreCaseIsCultureInvariant()
    {
        List<string> offenders = [.. AllGenerated()
            .Where(g => g.Attribute.Contains("IgnoreCase", StringComparison.Ordinal) && !g.Attribute.Contains("CultureInvariant", StringComparison.Ordinal))
            .Select(g => $"{g.File}: {g.Method}")];
        Assert.True(offenders.Count == 0, "IgnoreCase without CultureInvariant on: " + string.Join(", ", offenders));
    }

    [Fact]
    public void GeneratedRegexDoesNotAskForCompiled()
    {
        // The source generator ignores RegexOptions.Compiled; carrying it is a lie about what the code does.
        List<string> offenders = [.. AllGenerated().Where(g => g.Attribute.Contains("Compiled", StringComparison.Ordinal)).Select(g => $"{g.File}: {g.Method}")];
        Assert.True(offenders.Count == 0, "Compiled on: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryGeneratedRegexCarriesTheSharedTimeout()
    {
        List<string> offenders = [.. AllGenerated().Where(g => !g.Attribute.Contains("RegexTimeout.Milliseconds", StringComparison.Ordinal)).Select(g => $"{g.File}: {g.Method}")];
        Assert.True(offenders.Count == 0, "no shared timeout on: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoRegexIsBuiltWithNewOrCalledStaticallyWithoutTheSharedTimeout()
    {
        List<string> offenders = [];
        foreach (string file in SourceFiles())
        {
            string text = File.ReadAllText(file);
            string rel = Path.GetRelativePath(RepoPaths.Root, file).Replace('\\', '/');
            if (NewRegexCall().IsMatch(text))
            {
                offenders.Add($"{rel}: new Regex(");
            }
            foreach (Match call in StaticRegexCall().Matches(text))
            {
                // the call text up to the end of its statement line must name the shared timeout
                int end = text.IndexOf(';', call.Index);
                string statement = text[call.Index..(end < 0 ? text.Length : end)];
                if (!statement.Contains("RegexTimeout.", StringComparison.Ordinal))
                {
                    offenders.Add($"{rel}: {call.Value}");
                }
            }
        }
        Assert.True(offenders.Count == 0, "regex without the shared timeout: " + string.Join("; ", offenders));
    }

    [GeneratedRegex(@"\bnew\s+(?:System\.Text\.RegularExpressions\.)?Regex\s*\(", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex NewRegexCall();

    [GeneratedRegex(@"(?<![\w.])(?:System\.Text\.RegularExpressions\.)?Regex\.(?:Replace|IsMatch|Match|Matches|Split|Count|EnumerateMatches)\(", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex StaticRegexCall();
}
