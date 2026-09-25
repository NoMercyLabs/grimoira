using System.Diagnostics;
using System.Text.RegularExpressions;
using Aitm.TestSupport;
using Xunit;

namespace Aitm.Layout.Tests;

// Permanent guard for the bug class this slice found twice (add --why, fixed in 4fc7387; brain learn
// --facet, fixed alongside this guard): a flag the oracle's aitm.cs case read is no longer read anywhere
// in today's case for the same verb, so it is silently dropped.
//
// The rule: for every top-level `case "<verb>":` in aitm.cs's `switch (cmd)`, and every nested
// `case "<sub>":` in BrainCmd's `switch (sub)`, collect the `--flag` names read directly in that case's
// block, plus (recursively, up to 3 levels) the flags read inside any locally-declared function the
// block calls by name. Compare that set against the same extraction run over aitm.cs as it stood at
// OldVsNewCli.OracleCommit (before slice 24 moved any verb into a tool class). Every flag the oracle
// read for a verb must still be read for that verb today — a verb losing a flag fails this test, even
// if no other test happens to exercise that flag's value.
public class Slice24FlagAuditGuardTests
{
    private static readonly Regex FlagRegex = new(
        @"(?:GetFlag|HasFlag)\(\s*""(--[\w-]+)""\s*\)|\ba\.Contains\(\s*""(--[\w-]+)""\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex CaseNameRegex = new(@"case\s+""([^""]+)""\s*:", RegexOptions.Compiled);

    private static readonly Regex CallRegex = new(@"\b([A-Z][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void NoTopLevelOrBrainVerbLosesAFlagTheOracleRead()
    {
        string currentSource = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Aitm.Server", "Data", "CliDispatch.cs"));
        string oracleSource = ReadOracleSource();

        Dictionary<string, HashSet<string>> oldTopLevel = ExtractVerbFlags(oracleSource, FindTopLevelSwitchBody(oracleSource));
        Dictionary<string, HashSet<string>> newTopLevel = ExtractVerbFlags(currentSource, FindTopLevelSwitchBody(currentSource));
        Dictionary<string, HashSet<string>> oldBrain = ExtractVerbFlags(oracleSource, FindNestedSwitchBody(oracleSource, "BrainCmd"));
        Dictionary<string, HashSet<string>> newBrain = ExtractVerbFlags(currentSource, FindNestedSwitchBody(currentSource, "BrainCmd"));

        List<string> missing = new();
        CollectMissing(oldTopLevel, newTopLevel, "", missing);
        CollectMissing(oldBrain, newBrain, "brain ", missing);

        Assert.True(missing.Count == 0, "verb(s) lost a flag the oracle read:\n" + string.Join("\n", missing));
    }

    private static void CollectMissing(
        Dictionary<string, HashSet<string>> oldFlags, Dictionary<string, HashSet<string>> newFlags,
        string verbPrefix, List<string> missing)
    {
        foreach ((string verb, HashSet<string> oldSet) in oldFlags)
        {
            // A verb dropped entirely (e.g. `loop`) is GoldenListsTests's job, not this one's.
            if (!newFlags.TryGetValue(verb, out HashSet<string>? newSet)) continue;
            foreach (string flag in oldSet)
                if (!newSet.Contains(flag))
                    missing.Add($"{verbPrefix}{verb}: lost {flag}");
        }
    }

    // Case blocks run from one `case "x":` to the next `case`/`default` at the same indent, which is how
    // every switch in aitm.cs is laid out (verified by GoldenListsTests' own case-name matching).
    private static Dictionary<string, HashSet<string>> ExtractVerbFlags(string fullSource, string switchBody)
    {
        Dictionary<string, HashSet<string>> result = new(StringComparer.Ordinal);
        MatchCollection caseMatches = CaseNameRegex.Matches(switchBody);
        for (int i = 0; i < caseMatches.Count; i++)
        {
            Match m = caseMatches[i];
            int blockStart = m.Index + m.Length;
            int blockEnd = i + 1 < caseMatches.Count ? caseMatches[i + 1].Index : switchBody.Length;
            string block = switchBody[blockStart..blockEnd];
            string verb = m.Groups[1].Value;
            // A verb whose case now only prints the dropped-verb message (section 2.1's rule: "for one
            // minor version, a dropped verb prints 'removed in 0.4: <reason>' and exits 2") is a full
            // removal, same as a verb missing from the switch entirely (`loop`, already handled below) —
            // not a flag silently lost while the verb's logic stayed put. `selftest` took this path in
            // this slice: its case still exists (so the golden-list style "still exists" checks are
            // unaffected) but the 63 checks that used to read these flags via BrainLearn/StageCmd moved
            // into the test projects, so the flags themselves have nowhere left to be read from.
            if (block.Contains("removed in 0.4:", StringComparison.Ordinal)) continue;
            HashSet<string> flags = FlagsIn(block, fullSource, new HashSet<string>(StringComparer.Ordinal), 0);
            if (result.TryGetValue(verb, out HashSet<string>? existing)) existing.UnionWith(flags);
            else result[verb] = flags;
        }
        return result;
    }

    private static HashSet<string> FlagsIn(string block, string fullSource, HashSet<string> visited, int depth)
    {
        HashSet<string> flags = new(StringComparer.Ordinal);
        foreach (Match fm in FlagRegex.Matches(block))
            flags.Add(fm.Groups[1].Success ? fm.Groups[1].Value : fm.Groups[2].Value);

        if (depth >= 3) return flags;
        foreach (Match cm in CallRegex.Matches(block))
        {
            string name = cm.Groups[1].Value;
            if (!visited.Add(name)) continue;
            string? body = FindFunctionBody(fullSource, name);
            if (body is not null)
                flags.UnionWith(FlagsIn(body, fullSource, visited, depth + 1));
        }
        return flags;
    }

    // Matches a top-level local-function declaration by name (e.g. `void AddCmd()`, `string? Backup(...)`,
    // `List<string> ListCandidates(...)`) and returns its brace-balanced body, or null for anything that
    // is not a declaration here (a tool constructor, a BCL call, a call with no local declaration).
    private static string? FindFunctionBody(string source, string name)
    {
        Match decl = Regex.Match(source, $@"(?m)^[\w<>\[\],\?\s]+\b{Regex.Escape(name)}\s*\([^\)]*\)\s*\r?\n?\{{");
        if (!decl.Success) return null;
        int openBrace = decl.Index + decl.Length - 1;
        return ExtractBalanced(source, openBrace);
    }

    private static string FindTopLevelSwitchBody(string source)
    {
        Match anchor = Regex.Match(source, @"switch\s*\(cmd\)");
        int openBrace = source.IndexOf('{', anchor.Index + anchor.Length);
        return ExtractBalanced(source, openBrace);
    }

    // BrainCmd and StageCmd both declare `switch (sub)`, so the anchor has to be scoped to the named
    // function's own body first.
    private static string FindNestedSwitchBody(string source, string functionName)
    {
        string? functionBody = FindFunctionBody(source, functionName)
            ?? throw new InvalidOperationException($"{functionName} not found in the given source");
        Match anchor = Regex.Match(functionBody, @"switch\s*\(sub\)");
        int openBrace = functionBody.IndexOf('{', anchor.Index + anchor.Length);
        return ExtractBalanced(functionBody, openBrace);
    }

    private static string ExtractBalanced(string source, int openBraceIndex)
    {
        int depth = 0;
        for (int i = openBraceIndex; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[(openBraceIndex + 1)..i];
            }
        }
        throw new InvalidOperationException("unbalanced braces starting at " + openBraceIndex);
    }

    // The oracle text is read straight from git rather than a checked-out worktree — OldVsNewCli already
    // owns building/caching the oracle *binary*; this only needs the *source text* of one file.
    private static string ReadOracleSource()
    {
        ProcessStartInfo psi = new("git", $"-C \"{RepoPaths.Root}\" show {OldVsNewCli.OracleCommit}:aitm.cs")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("could not start git show");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git show {OldVsNewCli.OracleCommit}:aitm.cs exited {process.ExitCode}");
        return stdout;
    }
}
