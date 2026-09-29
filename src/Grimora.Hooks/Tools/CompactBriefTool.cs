using Grimora.Store.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grimora.Hooks.Data;

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
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string? transcriptPath = GetString(payload, "transcript_path");
            if (transcriptPath is null || !File.Exists(transcriptPath)) return "";

            string instance = HookPaths.ResolveInstance(GetString(payload, "cwd"), projectDir);
            string? sessionId = GetString(payload, "session_id");
            List<JsonElement> entries = ReadEntries(transcriptPath);
            List<string> files = TouchedFiles(entries);
            List<(string status, string content)> todos = OpenTodos(entries);
            List<ClassifiedEntry> classified = ClassifyEntries(entries);
            List<(string root, string branch, int dirty, string head)> repos = RepoState(files);

            string ledgerPath = HookPaths.LedgerPath(instance, sessionId);
            string ledger = BuildLedger(classified);
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

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static List<JsonElement> ReadEntries(string path)
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

    /// <summary>the owner's own words, whichever shape they arrived in: typed, a mid-turn queued message, or
    /// an AskUserQuestion answer. Used both to cut <see cref="Directives"/> at the compaction boundary and,
    /// at restore time, to judge whether the ledger carried them all.</summary>
    internal static bool IsOwnerFamily(string who) => who is "the owner" or "the owner (mid-turn)" or "the owner (answer)";

    private static bool IsNoiseEntry(JsonElement e) =>
        (e.TryGetProperty("isSidechain", out JsonElement sc) && sc.ValueKind == JsonValueKind.True) ||
        (e.TryGetProperty("isMeta", out JsonElement meta) && meta.ValueKind == JsonValueKind.True);

    internal static int LastCompactionBoundaryIndex(List<JsonElement> entries)
    {
        int boundary = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            JsonElement e = entries[i];
            if (GetString(e, "type") != "user") continue;
            string text = ExtractUserText(e).Trim();
            if (ContinuedSummaryStart().IsMatchOrFalse(text)) boundary = i;
        }
        return boundary;
    }

    private static string ExtractUserText(JsonElement e)
    {
        if (!e.TryGetProperty("message", out JsonElement msg) || !msg.TryGetProperty("content", out JsonElement content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        List<string> parts = [];
        foreach (JsonElement b in content.EnumerateArray())
        {
            if (GetString(b, "type") == "text") parts.Add(GetString(b, "text") ?? "");
        }
        return string.Join(" ", parts);
    }

    /// <summary>If <paramref name="e"/> is a user turn answering one of <paramref name="askUserQuestionToolIds"/>,
    /// the question/answer pairs Claude Code recorded in its own <c>toolUseResult.answers</c> map — the
    /// structured record, not the "The user answered: ..." string glued together for display.</summary>
    private static List<(string question, string answer)>? TryGetAnswers(JsonElement e, HashSet<string> askUserQuestionToolIds)
    {
        if (!e.TryGetProperty("message", out JsonElement msg) || !msg.TryGetProperty("content", out JsonElement content)) return null;
        if (content.ValueKind != JsonValueKind.Array) return null;

        bool answersAskUserQuestion = false;
        foreach (JsonElement b in content.EnumerateArray())
        {
            if (GetString(b, "type") != "tool_result") continue;
            string? toolUseId = GetString(b, "tool_use_id");
            if (toolUseId is not null && askUserQuestionToolIds.Contains(toolUseId)) answersAskUserQuestion = true;
        }
        if (!answersAskUserQuestion) return null;

        if (!e.TryGetProperty("toolUseResult", out JsonElement tur) || !tur.TryGetProperty("answers", out JsonElement answersEl)) return null;
        if (answersEl.ValueKind != JsonValueKind.Object) return null;

        List<(string question, string answer)> outp = [];
        foreach (JsonProperty p in answersEl.EnumerateObject())
        {
            outp.Add((p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : ""));
        }
        return outp;
    }

    private static IEnumerable<JsonElement> BlocksOf(JsonElement e)
    {
        if (!e.TryGetProperty("message", out JsonElement msg) || !msg.TryGetProperty("content", out JsonElement content)) yield break;
        if (content.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement b in content.EnumerateArray()) yield return b;
    }

    /// <summary>One transcript entry, recognised as the owner's, a peer's, or Arc's own words.
    /// <paramref name="Index"/> is the entry's position in the transcript the classifier read, so a caller
    /// can cut at a compaction boundary computed on the same list.</summary>
    internal readonly record struct ClassifiedEntry(int Index, string Timestamp, string Who, string Text);

    // The one recogniser for what counts as said, in transcript order, with its own dedupe rules applied
    // once: LedgerEntries and Directives (nee OwnerEntriesSince) used to each run their own pass and could
    // drift — a mid-turn queued message landed after every typed message regardless of when it was queued,
    // and a duplicate queued_command could be counted twice by one and once by the other. The verbatim
    // ledger PreCompact writes on every compaction takes every classified entry, never cut, never limited
    // to the last few; the brief takes only the the owner-family entries after the compaction boundary. Order
    // is transcript order, which is also why re-running this on the same transcript is deterministic.
    internal static List<ClassifiedEntry> ClassifyEntries(List<JsonElement> entries)
    {
        List<ClassifiedEntry> outp = [];
        HashSet<string> seenAssistantText = [];
        HashSet<string> seenQueued = [];
        HashSet<string> askUserQuestionToolIds = [];

        for (int i = 0; i < entries.Count; i++)
        {
            JsonElement e = entries[i];
            if (e.ValueKind != JsonValueKind.Object || IsNoiseEntry(e)) continue;
            string type = GetString(e, "type") ?? "";
            string timestamp = GetString(e, "timestamp") ?? "";

            if (type == "user")
            {
                List<(string question, string answer)>? answers = TryGetAnswers(e, askUserQuestionToolIds);
                if (answers is { Count: > 0 })
                {
                    foreach ((string question, string answer) in answers)
                    {
                        outp.Add(new ClassifiedEntry(i, timestamp, "the owner (answer)", $"{question}: {answer}"));
                    }
                    continue;
                }

                string trimmed = ExtractUserText(e).Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('<') || trimmed.StartsWith("Caveat:", StringComparison.Ordinal)) continue;
                if (ContinuedSummaryStart().IsMatchOrFalse(trimmed)) continue;
                if (HookFeedbackNotice().IsMatchOrFalse(trimmed[..Math.Min(60, trimmed.Length)])) continue;
                outp.Add(new ClassifiedEntry(i, timestamp, "the owner", trimmed));
            }
            else if (type == "assistant")
            {
                string? msgId = e.TryGetProperty("message", out JsonElement m) && m.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                foreach (JsonElement b in BlocksOf(e))
                {
                    string? btype = GetString(b, "type");
                    if (btype == "text")
                    {
                        string text = (GetString(b, "text") ?? "").Trim();
                        if (text.Length == 0) continue;
                        if (!seenAssistantText.Add($"{msgId}\u0000{text}")) continue;
                        outp.Add(new ClassifiedEntry(i, timestamp, "Arc", text));
                    }
                    else if (btype == "tool_use" && GetString(b, "name") == "AskUserQuestion")
                    {
                        string? toolId = GetString(b, "id");
                        if (toolId is not null) askUserQuestionToolIds.Add(toolId);
                        string question = AskUserQuestionText(b);
                        if (question.Length > 0) outp.Add(new ClassifiedEntry(i, timestamp, "Arc (question)", question));
                    }
                }
            }
            else if (type == "attachment")
            {
                if (!e.TryGetProperty("attachment", out JsonElement att)) continue;
                if (GetString(att, "type") != "queued_command") continue;
                if (GetString(att, "commandMode") == "task-notification") continue;

                string dedupeKey = GetString(att, "source_uuid") ?? GetString(e, "uuid") ?? "";
                if (dedupeKey.Length > 0 && !seenQueued.Add(dedupeKey)) continue;

                string prompt = (GetString(att, "prompt") ?? "").Trim();
                bool isPeer = att.TryGetProperty("origin", out JsonElement origin) && GetString(origin, "kind") == "peer";
                if (isPeer)
                {
                    string sender = FromNameAttribute().MatchOrEmpty(prompt) is { Success: true } m2
                        ? m2.Groups[1].Value
                        : GetString(origin, "name") ?? "unknown";
                    outp.Add(new ClassifiedEntry(i, timestamp, $"Peer {sender}", prompt));
                }
                else
                {
                    outp.Add(new ClassifiedEntry(i, timestamp, "the owner (mid-turn)", prompt));
                }
            }
        }
        return outp;
    }

    private static string AskUserQuestionText(JsonElement toolUseBlock)
    {
        if (!toolUseBlock.TryGetProperty("input", out JsonElement input)) return "";
        if (!input.TryGetProperty("questions", out JsonElement questions) || questions.ValueKind != JsonValueKind.Array) return "";
        List<string> parts = [];
        foreach (JsonElement q in questions.EnumerateArray())
        {
            string? text = GetString(q, "question");
            if (!string.IsNullOrEmpty(text)) parts.Add(text);
        }
        return string.Join(" | ", parts);
    }

    // One block per entry, not one line: "- [ts] who: text" broke the moment text itself held a line
    // break (a multi-line message written verbatim, which item B requires) — the restore-time loss regex
    // then miscounted, unable to tell a continuation line from the next entry.
    private static string BuildLedger(List<ClassifiedEntry> classified)
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
            "",
        ];
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
            if (!b.TryGetProperty("input", out JsonElement input)) continue;
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

    [GeneratedRegex(@"(^|[\\/])(scratchpad|\.?scratch|Temp|tmp)([\\/]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout.Milliseconds)]
    private static partial Regex ScratchPath();
    [GeneratedRegex(@"^(This session is being continued|Caveat: The messages below|\[Request interrupted)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ContinuedSummaryStart();
    [GeneratedRegex("hook (feedback|additional context)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout.Milliseconds)]
    private static partial Regex HookFeedbackNotice();
    [GeneratedRegex("from-name=\"([^\"]*)\"", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex FromNameAttribute();
}
