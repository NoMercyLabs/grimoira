using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aitm.Hooks.Data;

namespace Aitm.Hooks.Tools;

/// <summary>
/// PreCompact handler, ported verbatim from compact-brief.mjs (RESTRUCTURE.md slice 20, "Hooks, part
/// 1"): a compaction summary is a lossy retelling, so the facts that must not be lost — the user's own
/// words, which repos are dirty and on what branch, which files changed, and what is still open — are
/// written to disk before it happens. <see cref="CompactRestoreTool"/> reads them back once on the far
/// side. Never blocks or crashes a session: any error returns an empty string, same as the .mjs's
/// fail-open catch plus <c>process.exit(0)</c>.
/// </summary>
public static partial class CompactBriefTool
{
    // Scratch files are deliberately throwaway; carrying them across a compaction makes the real edits
    // harder to see and invites the next turn to treat a temp script as project work.

    // An earlier compaction's summary is injected as a user turn. Quoting it back as a directive would
    // let each compaction copy the previous one forward until the brief is nothing but old summaries.

    public static string Execute(string stdin) => Execute(stdin, projectDir: null);

    /// <param name="projectDir">The project the caller resolved (<see cref="HookPaths.ProjectDir"/>), or null.</param>
    public static string Execute(string stdin, string? projectDir)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string? transcriptPath = GetString(payload, "transcript_path");
            if (transcriptPath is null || !File.Exists(transcriptPath)) return "";

            string instance = HookPaths.ResolveInstance(GetString(payload, "cwd"), projectDir);
            List<JsonElement> entries = ReadEntries(transcriptPath);
            List<string> files = TouchedFiles(entries);
            List<(string status, string content)> todos = OpenTodos(entries);
            List<string> said = Directives(entries);
            List<(string root, string branch, int dirty, string head)> repos = RepoState(files);

            string brief = BuildBrief(said, repos, files, todos);

            string outPath = HookPaths.BriefPath(instance, GetString(payload, "session_id"));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllText(outPath, brief);
            }
            catch
            {
                // best effort
            }

            // PLAIN TEXT, not a hookSpecificOutput envelope. PreCompact collects each hook's raw stdout;
            // the envelope every other hook event takes fails schema validation here.
            return "The summary must carry these verbatim, not paraphrased: the exact wording of any standing " +
                "directive, every file path already changed, the branch each repo is on, what is committed " +
                "versus still uncommitted, and every unfinished item. Concrete anchors survive compaction; " +
                $"impressions do not.\n\n{brief}\n";
        }
        catch
        {
            // fail open — a compaction must never be blocked by bookkeeping
            return "";
        }
    }

    private static string BuildBrief(
        List<string> said,
        List<(string root, string branch, int dirty, string head)> repos,
        List<string> files,
        List<(string status, string content)> todos)
    {
        List<string> lines = ["# Carried across compaction", ""];

        if (said.Count > 0)
        {
            lines.Add("## What the owner actually asked for, in his words");
            lines.Add("");
            foreach (string s in said) lines.Add($"- \"{s}\"");
            lines.Add("");
        }

        if (repos.Count > 0)
        {
            lines.Add("## Repo state at compaction");
            lines.Add("");
            foreach ((string root, string branch, int dirty, string head) in repos)
            {
                string dirtyText = dirty > 0 ? $", {dirty} uncommitted path(s)" : ", clean";
                lines.Add($"- {root} — branch `{branch}`, HEAD {head}{dirtyText}");
            }
            lines.Add("");
        }

        if (files.Count > 0)
        {
            // Grouped, not enumerated. A long campaign touches hundreds of files, and 40 paths followed
            // by "587 more" is the worst of both: too long to read, and it hides which areas were
            // worked in. Counts per directory answer "where was I".
            Dictionary<string, int> byDir = [];
            foreach (string f in files)
            {
                string d = Dirname(f.Replace('\\', '/'));
                byDir[d] = byDir.GetValueOrDefault(d) + 1;
            }
            List<KeyValuePair<string, int>> dirs = [.. byDir.OrderByDescending(kv => kv.Value)];
            lines.Add($"## Where the {files.Count} changed file(s) are");
            lines.Add("");
            foreach (KeyValuePair<string, int> kv in dirs.Take(12)) lines.Add($"- {kv.Key}  ({kv.Value})");
            if (dirs.Count > 12) lines.Add($"- … {dirs.Count - 12} more director(ies)");
            lines.Add("");
            lines.Add("Most recent:");
            lines.Add("");
            foreach (string f in files.Skip(Math.Max(0, files.Count - 8))) lines.Add($"- {f}");
            lines.Add("");
        }

        if (todos.Count > 0)
        {
            lines.Add("## Still open");
            lines.Add("");
            foreach ((string status, string content) in todos) lines.Add($"- [{status}] {content}");
            lines.Add("");
        }

        return string.Join("\n", lines);
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static List<JsonElement> ReadEntries(string path)
    {
        List<JsonElement> outp = [];
        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using JsonDocument d = JsonDocument.Parse(line);
                outp.Add(d.RootElement.Clone());
            }
            catch
            {
                // partial write
            }
        }
        return outp;
    }

    private static IEnumerable<JsonElement> Blocks(List<JsonElement> entries)
    {
        foreach (JsonElement e in entries)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!e.TryGetProperty("message", out JsonElement msg)) continue;
            if (!msg.TryGetProperty("content", out JsonElement content)) continue;
            if (content.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement b in content.EnumerateArray()) yield return b;
        }
    }

    // The user's own words are the directive; a summary reliably keeps the task and drops the
    // constraints.
    private static List<string> Directives(List<JsonElement> entries)
    {
        List<string> said = [];
        foreach (JsonElement e in entries)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (GetString(e, "type") != "user") continue;
            if (e.TryGetProperty("isMeta", out JsonElement metaEl) && metaEl.ValueKind == JsonValueKind.True) continue;

            string text = "";
            if (e.TryGetProperty("message", out JsonElement msg) && msg.TryGetProperty("content", out JsonElement content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString() ?? "";
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    List<string> parts = [];
                    foreach (JsonElement b in content.EnumerateArray())
                    {
                        if (GetString(b, "type") == "text") parts.Add(GetString(b, "text") ?? "");
                    }
                    text = string.Join(" ", parts);
                }
            }

            string trimmed = text.Trim();
            if (trimmed.Length < 12 || trimmed.StartsWith('<') || trimmed.StartsWith("Caveat:", StringComparison.Ordinal)) continue;
            if (ContinuedSummaryStart().IsMatch(trimmed)) continue;
            if (HookFeedbackNotice().IsMatch(trimmed[..Math.Min(60, trimmed.Length)])) continue;

            string cleaned = ConsecutiveWhitespace().Replace(trimmed, " ");
            said.Add(cleaned.Length > 400 ? cleaned[..400] : cleaned);
        }
        return said.Count > 4 ? said[^4..] : said;
    }

    private static List<string> TouchedFiles(List<JsonElement> entries)
    {
        HashSet<string> seen = [];
        List<string> files = [];
        foreach (JsonElement b in Blocks(entries))
        {
            if (GetString(b, "type") != "tool_use") continue;
            string? name = GetString(b, "name");
            if (name is not ("Edit" or "Write" or "NotebookEdit")) continue;
            if (!b.TryGetProperty("input", out JsonElement input)) continue;
            string? fp = GetString(input, "file_path");
            if (fp is null || ScratchPath().IsMatch(fp)) continue;
            if (seen.Add(fp)) files.Add(fp);
        }
        return files;
    }

    private static List<(string status, string content)> OpenTodos(List<JsonElement> entries)
    {
        List<(string status, string content)> todos = [];
        foreach (JsonElement b in Blocks(entries))
        {
            if (GetString(b, "type") != "tool_use" || GetString(b, "name") != "TodoWrite") continue;
            if (!b.TryGetProperty("input", out JsonElement input)) continue;
            if (!input.TryGetProperty("todos", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) continue;

            todos.Clear();
            foreach (JsonElement t in arr.EnumerateArray())
            {
                todos.Add((GetString(t, "status") ?? "", GetString(t, "content") ?? ""));
            }
        }
        return [.. todos.Where(t => t.status != "completed")];
    }

    private static string Dirname(string path)
    {
        int idx = path.LastIndexOf('/');
        return idx < 0 ? "." : path[..idx];
    }

    // Which repos still hold work, and on what branch. "Which branch am I on" is a HARD rule and a
    // summary never carries it, so it gets re-asked or, worse, assumed.
    private static List<(string root, string branch, int dirty, string head)> RepoState(List<string> files)
    {
        HashSet<string> roots = [];
        foreach (string f in files)
        {
            string dir = Dirname(f.Replace('\\', '/'));
            for (int i = 0; i < 8 && dir.Length > 3; i++)
            {
                if (Directory.Exists(Path.Combine(dir, ".git"))) { roots.Add(dir); break; }
                dir = Dirname(dir);
            }
        }

        List<(string root, string branch, int dirty, string head)> outp = [];
        foreach (string root in roots)
        {
            try
            {
                string branch = RunGit(root, "branch --show-current").Trim();
                string dirty = RunGit(root, "status --porcelain").Trim();
                string head = RunGit(root, "log -1 --format=%h %s").Trim();
                int dirtyCount = dirty.Length == 0 ? 0 : dirty.Split('\n').Length;
                outp.Add((root, branch, dirtyCount, head));
            }
            catch
            {
                // not a repo, or git unavailable
            }
        }
        return outp;
    }

    private static string RunGit(string root, string arguments)
    {
        ProcessStartInfo psi = new("git", $"-C \"{root}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("git could not start");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git exited non-zero");
        return output;
    }

    [GeneratedRegex(@"(^|[\\/])(scratchpad|\.?scratch|Temp|tmp)([\\/]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScratchPath();
    [GeneratedRegex(@"^(This session is being continued|Caveat: The messages below|\[Request interrupted)")]
    private static partial Regex ContinuedSummaryStart();
    [GeneratedRegex("hook (feedback|additional context)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HookFeedbackNotice();
    [GeneratedRegex(@"\s+")]
    private static partial Regex ConsecutiveWhitespace();
}
