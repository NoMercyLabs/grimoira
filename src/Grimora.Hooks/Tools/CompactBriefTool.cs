using Grimora.Store.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grimora.Hooks.Data;
using static Grimora.Store.Data.JsonShape;
using static Grimora.Memory.Data.TranscriptClassifier;

namespace Grimora.Hooks.Tools;

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
        string? instance = null;
        string? sessionId = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string? transcriptPath = GetString(payload, "transcript_path");
            if (transcriptPath is null || !File.Exists(transcriptPath)) return "";

            instance = HookPaths.ResolveInstance(GetString(payload, "cwd"), projectDir);
            sessionId = GetString(payload, "session_id");
            List<JsonElement> entries = ReadEntries(transcriptPath);
            List<string> files = TouchedFiles(entries);
            List<(string status, string content)> todos = OpenTodos(entries);
            (List<ClassifiedEntry> classified, int skipped) = ClassifyEntries(entries);
            List<(string root, string branch, int dirty, string head)> repos = RepoState(files);

            string ledgerPath = HookPaths.LedgerPath(instance, sessionId);
            string ledger = BuildLedger(classified, skipped);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
                File.WriteAllText(ledgerPath, ledger);
            }
            catch
            {
                // best effort
            }

            int boundary = LastCompactionBoundaryIndex(entries);
            List<string> said = Directives(classified, boundary, ledgerPath);

            string brief = BuildBrief(said, repos, files, todos, ledgerPath);

            string outPath = HookPaths.BriefPath(instance, sessionId);
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
        catch (Exception ex)
        {
            // No longer fail open silently: a real 3,076-line transcript once hit an unguarded shape and
            // Execute returned "" with nothing written anywhere, so the loss was invisible until someone
            // went looking. The compaction itself must still never be blocked, so this still returns —
            // just never blank, and always with a trail on disk.
            return WriteFailure(ex, instance, sessionId, projectDir);
        }
    }

    /// <summary>Never silent: writes the exception and its stack to
    /// <c>&lt;instance&gt;/compact/&lt;sessionId&gt;.error.log</c> and returns plain text starting
    /// "GRIMORA COMPACTION LEDGER FAILED:" naming the exception type, its message, and the log path.
    /// <see cref="CompactRestoreTool"/> checks this same log's timestamp against the brief's.</summary>
    private static string WriteFailure(Exception ex, string? instance, string? sessionId, string? projectDir)
    {
        string message = $"GRIMORA COMPACTION LEDGER FAILED: {ex.GetType().Name}: {ex.Message}";
        try
        {
            string inst = instance ?? HookPaths.ResolveInstance(null, projectDir);
            string logPath = ErrorLogPath(inst, sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}\n");
            return $"{message}\nDetails: {logPath}\n";
        }
        catch
        {
            // even the failure report is best effort — a compaction must never be blocked by bookkeeping
            return $"{message}\n";
        }
    }

    /// <summary>Same path shape as <see cref="HookPaths.BriefPath"/> and <see cref="HookPaths.LedgerPath"/>
    /// (duplicated the same way <see cref="HookPaths"/> itself duplicates data-dir resolution rather than
    /// widening that class's own contract for one caller).</summary>
    internal static string ErrorLogPath(string instance, string? sessionId)
    {
        string sid = string.IsNullOrEmpty(sessionId) ? "x" : sessionId;
        if (sid.Length > 64) sid = sid[..64];
        return Path.Combine(HookPaths.InstanceDir(instance), "compact", $"{sid}.error.log");
    }

    private static string BuildBrief(
        List<string> said,
        List<(string root, string branch, int dirty, string head)> repos,
        List<string> files,
        List<(string status, string content)> todos,
        string ledgerPath)
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

        lines.Add("## Full verbatim record");
        lines.Add("");
        lines.Add($"Every entry - the owner's, the peers', and Arc's own replies - is kept whole in {ledgerPath}");
        lines.Add("");

        return string.Join("\n", lines);
    }

    private static IEnumerable<JsonElement> Blocks(List<JsonElement> entries)
    {
        foreach (JsonElement e in entries)
        {
            if (!TryGetObjectProperty(e, "message", out JsonElement msg)) continue;
            if (!TryGetObjectProperty(msg, "content", out JsonElement content)) continue;
            if (content.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement b in content.EnumerateArray()) yield return b;
        }
    }

    // The user's own words are the directive; a summary reliably keeps the task and drops the
    // constraints. Everything the owner said since the last compaction boundary carries whole — no
    // per-message cut, no "last 4 only" — because a mid-turn instruction 5 messages back is exactly as
    // binding as the most recent one. Only when the combined text would blow the brief's own budget does
    // this fall back to newest-first, and even then it says so instead of silently dropping the rest.
    private const int DirectiveBudgetChars = 24_000;

    // Filters the one classifier's output down to what's new since the last compaction, in transcript
    // order (ClassifyEntries already walks the transcript in one pass, so no separate "typed" then
    // "mid-turn" loops to drift out of order or double-count a dedupe).
    private static List<string> Directives(List<ClassifiedEntry> classified, int boundary, string ledgerPath)
    {
        List<string> said = [.. classified.Where(c => c.Index > boundary && IsOwnerFamily(c.Who)).Select(c => c.Text)];

        int total = said.Sum(s => s.Length);
        if (total <= DirectiveBudgetChars) return said;

        List<string> kept = [];
        int used = 0;
        int cutIndex = said.Count;
        for (int i = said.Count - 1; i >= 0; i--)
        {
            int len = said[i].Length;
            if (used + len > DirectiveBudgetChars && kept.Count > 0) { cutIndex = i + 1; break; }
            kept.Insert(0, said[i]);
            used += len;
            cutIndex = i;
        }

        if (cutIndex > 0)
        {
            kept.Add($"{cutIndex} earlier messages since the last compaction are in the ledger: {ledgerPath}");
        }
        return kept;
    }

    // One block per entry, not one line: "- [ts] who: text" broke the moment text itself held a line
    // break (a multi-line message written verbatim, which item B requires) — the restore-time loss regex
    // then miscounted, unable to tell a continuation line from the next entry.
    private static string BuildLedger(List<ClassifiedEntry> classified, int skipped)
    {
        int owner = classified.Count(c => c.Who == "the owner");
        int midTurn = classified.Count(c => c.Who == "the owner (mid-turn)");
        int answer = classified.Count(c => c.Who == "the owner (answer)");
        int arc = classified.Count(c => c.Who == "Arc");
        int arcQuestion = classified.Count(c => c.Who == "Arc (question)");
        int peer = classified.Count(c => c.Who.StartsWith("Peer ", StringComparison.Ordinal));

        List<string> lines =
        [
            "# Compaction ledger — verbatim, never truncated",
            "",
            $"Entries: {classified.Count} (the owner {owner}, the owner (mid-turn) {midTurn}, the owner (answer) {answer}, " +
                $"Arc {arc}, Arc (question) {arcQuestion}, Peer {peer})",
        ];
        if (skipped > 0) lines.Add($"Skipped: {skipped}");
        lines.Add("");
        foreach (ClassifiedEntry c in classified)
        {
            lines.Add($"## {c.Who} — {c.Timestamp}");
            lines.Add("");
            lines.Add(c.Text);
            lines.Add("");
        }
        return string.Join("\n", lines) + "\n";
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
            if (!TryGetObjectProperty(b, "input", out JsonElement input)) continue;
            string? fp = GetString(input, "file_path");
            if (fp is null || ScratchPath().IsMatchOrFalse(fp)) continue;
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
            if (!TryGetObjectProperty(b, "input", out JsonElement input)) continue;
            if (!TryGetObjectProperty(input, "todos", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) continue;

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

    [GeneratedRegex(@"(^|[\\/])(scratchpad|\.?scratch|Temp|tmp)([\\/]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout.Milliseconds)]
    private static partial Regex ScratchPath();
}
