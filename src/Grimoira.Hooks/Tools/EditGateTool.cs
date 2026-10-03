using System.Globalization;
using System.Text;
using System.Text.Json;
using Grimoira.Facts.Tools;
using Grimoira.Hooks.Data;
using Grimoira.Memory.Tools;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using static Grimoira.Store.Data.JsonShape;

namespace Grimoira.Hooks.Tools;

/// <summary>
/// PreToolUse handler (Write|Edit|MultiEdit|NotebookEdit): the first time a session edits a file, the
/// rules and facts whose text matches the file's path land as context, before the edit, so the rule is
/// read when it still matters. A later edit of the same file in the same session adds nothing (a seen
/// list next to the compaction brief, <see cref="HookPaths.GateSeenPath"/>). Fail open everywhere: no
/// path, no store, no match or any error prints nothing and the edit goes ahead.
///
/// The queries are the MCP <c>rule</c> and <c>fact</c> tools themselves (<see cref="MemTool.ExecuteMcp"/>,
/// <see cref="QueryTool.ExecuteMcp"/>), built the way AllMcpTools builds them, so what the gate shows is
/// what a hand query for the same terms would show.
/// </summary>
public static class EditGateTool
{
    private const int MaxTerms = 12;
    private const int OutputCap = 1500;
    private static readonly HashSet<string> SkippedSegments = new(StringComparer.OrdinalIgnoreCase) { "src", "tests", "lib", "app" };

    public static string Execute(string stdin) => Execute(stdin, projectDir: null);

    /// <param name="projectDir">The project the caller resolved (<see cref="HookPaths.ProjectDir"/>), or null.</param>
    public static string Execute(string stdin, string? projectDir)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;

            string? filePath = GetToolInputPath(payload);
            if (string.IsNullOrWhiteSpace(filePath)) return "";

            string? cwd = GetString(payload, "cwd");
            string? sessionId = GetString(payload, "session_id");
            string instance = HookPaths.ResolveInstance(cwd, projectDir);
            if (!File.Exists(HookPaths.DbPath(instance))) return "";

            string file = Path.GetFullPath(filePath);
            string seenPath = HookPaths.GateSeenPath(instance, sessionId);
            if (IsSeen(seenPath, file)) return "";
            MarkSeen(seenPath, file);

            string proj = HookPaths.ProjectDir(cwd, projectDir);
            string terms = string.Join(' ', Terms(proj, file));
            if (terms.Length == 0) return "";

            (string rules, string facts) = Query(instance, terms);
            int ruleCount = CountHits(rules);
            int factCount = CountHits(facts);
            if (ruleCount == 0 && factCount == 0) return "";

            string relative = Path.GetRelativePath(proj, file).Replace('\\', '/');
            string context = Compose($"Grimoira: before you edit {relative}, these rules and facts apply.", rules, facts);
            Log(instance, sessionId, file, ruleCount, factCount, context.Length);

            var envelope = new
            {
                hookSpecificOutput = new { hookEventName = "PreToolUse", additionalContext = context },
            };
            return JsonSerializer.Serialize(envelope);
        }
        catch
        {
            // fail open: the edit never waits on a gate that broke
            return "";
        }
    }

    private static (string rules, string facts) Query(string instance, string terms)
    {
        using SqliteConnection connection = StoreConnection.Open(HookPaths.DbPath(instance));
        IUsageSignal usageSignal = new UsageSignal();
        string rules = Usable(new MemTool(usageSignal).ExecuteMcp(connection, terms));
        string facts = Usable(new QueryTool(usageSignal).ExecuteMcp(connection, terms));
        return (rules, facts);
    }

    /// <summary>The tools answer a miss in prose; the gate shows only hits.</summary>
    private static string Usable(string answer) =>
        answer.StartsWith("no rule matches", StringComparison.Ordinal)
        || answer.StartsWith("no confident answer", StringComparison.Ordinal)
        || answer.StartsWith("no usable query terms", StringComparison.Ordinal)
        || answer.StartsWith("memory not indexed", StringComparison.Ordinal)
            ? ""
            : answer.TrimEnd();

    private static int CountHits(string block) => block.Split('\n').Count(line => line.StartsWith("• ", StringComparison.Ordinal));

    /// <summary>Head, rules, facts; clipped to <see cref="OutputCap"/> by dropping facts lines first, then
    /// rules lines, never mid-line, and ending with an ellipsis when anything was cut.</summary>
    private static string Compose(string head, string rules, string facts)
    {
        List<string> ruleLines = Lines(rules);
        List<string> factLines = Lines(facts);
        bool cut = false;
        while (Length(head, ruleLines, factLines) > OutputCap && (factLines.Count > 0 || ruleLines.Count > 0))
        {
            if (factLines.Count > 0) factLines.RemoveAt(factLines.Count - 1);
            else ruleLines.RemoveAt(ruleLines.Count - 1);
            cut = true;
        }
        StringBuilder sb = new(head);
        foreach (string line in ruleLines) sb.Append('\n').Append(line);
        foreach (string line in factLines) sb.Append('\n').Append(line);
        if (cut) sb.Append('…');
        return sb.ToString();
    }

    private static List<string> Lines(string block) =>
        block.Length == 0 ? [] : [.. block.Replace("\r\n", "\n").Split('\n')];

    // Head, every kept line with its newline, plus one char for the ellipsis a cut adds.
    private static int Length(string head, List<string> ruleLines, List<string> factLines) =>
        head.Length + ruleLines.Sum(l => l.Length + 1) + factLines.Sum(l => l.Length + 1) + 1;

    /// <summary>Search terms from the path: the project folder, each folder under it (not src/tests/lib/app),
    /// the file stem split on '.', '-', '_' and CamelCase, and the extension. Lowercase, unique, at most
    /// <see cref="MaxTerms"/>.</summary>
    private static List<string> Terms(string projectDir, string file)
    {
        List<string> terms = [];
        string project = Path.GetFileName(projectDir.TrimEnd('\\', '/'));
        if (project.Length > 0) terms.Add(project);

        string relative = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
        string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string folder in segments.Take(segments.Length - 1))
        {
            if (folder == ".." || SkippedSegments.Contains(folder)) continue;
            terms.Add(folder);
        }

        string name = segments.Length > 0 ? segments[^1] : "";
        string extension = Path.GetExtension(name).TrimStart('.');
        string stem = Path.GetFileNameWithoutExtension(name);
        terms.AddRange(SplitWords(stem));
        if (extension.Length > 0) terms.Add(extension);

        return [.. terms
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 1)
            .Distinct()
            .Take(MaxTerms)];
    }

    private static IEnumerable<string> SplitWords(string stem)
    {
        foreach (string part in stem.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
        {
            StringBuilder word = new();
            foreach (char ch in part)
            {
                if (char.IsUpper(ch) && word.Length > 0 && !char.IsUpper(word[^1]))
                {
                    yield return word.ToString();
                    word.Clear();
                }
                word.Append(ch);
            }
            if (word.Length > 0) yield return word.ToString();
        }
    }

    private static bool IsSeen(string seenPath, string file)
    {
        if (!File.Exists(seenPath)) return false;
        return File.ReadLines(seenPath).Any(line => string.Equals(line.Trim(), file, PathComparison));
    }

    private static void MarkSeen(string seenPath, string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(seenPath)!);
            File.AppendAllText(seenPath, file + "\n");
        }
        catch
        {
            // best effort: a seen list that cannot be written only means the gate fires again
        }
    }

    /// <summary>One line per gate hit, the live proof a running session fired it.</summary>
    private static void Log(string instance, string? sessionId, string file, int rules, int facts, int chars)
    {
        try
        {
            string line = $"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)} PreToolUse {sessionId ?? "x"} {file} rules={rules} facts={facts} chars={chars}\n";
            File.AppendAllText(Path.Combine(HookPaths.InstanceDir(instance), "gate.log"), line);
        }
        catch
        {
            // best effort
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string? GetToolInputPath(JsonElement payload)
    {
        if (!TryGetObjectProperty(payload, "tool_input", out JsonElement input) || input.ValueKind != JsonValueKind.Object) return null;
        return GetString(input, "file_path") ?? GetString(input, "notebook_path");
    }
}
