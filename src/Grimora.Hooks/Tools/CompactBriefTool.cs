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
            List<(string timestamp, string who, string text)> ledgerEntries = LedgerEntries(entries);
            List<(string root, string branch, int dirty, string head)> repos = RepoState(files);

            string ledgerPath = HookPaths.LedgerPath(instance, sessionId);
            string ledger = BuildLedger(ledgerEntries);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
                File.WriteAllText(ledgerPath, ledger);
            }
            catch
            {
                // best effort
            }

            List<string> said = Directives(entries, ledgerPath);

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
    // constraints. Everything the owner said since the last compaction boundary carries whole — no
    // per-message cut, no "last 4 only" — because a mid-turn instruction 5 messages back is exactly as
    // binding as the most recent one. Only when the combined text would blow the brief's own budget does
    // this fall back to newest-first, and even then it says so instead of silently dropping the rest.
    private const int DirectiveBudgetChars = 24_000;

    private static List<string> Directives(List<JsonElement> entries, string ledgerPath)
    {
        int boundary = LastCompactionBoundaryIndex(entries);
        List<string> said = OwnerEntriesSince(entries, boundary);

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

    // Same recognition rules as LedgerEntries's the owner/the owner (mid-turn)/the owner (answer) branches, kept
    // separate because LedgerEntries already applied its own dedupe and does not preserve the transcript
    // index Directives needs to cut at the compaction boundary.
    private static List<string> OwnerEntriesSince(List<JsonElement> entries, int boundary)
    {
        List<string> said = [];
        HashSet<string> askUserQuestionToolIds = [];
        for (int i = 0; i < entries.Count; i++)
        {
            JsonElement e = entries[i];
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (IsNoiseEntry(e)) continue;

            string type = GetString(e, "type") ?? "";
            if (type == "assistant")
            {
                foreach (JsonElement b in BlocksOf(e))
                {
                    if (GetString(b, "type") == "tool_use" && GetString(b, "name") == "AskUserQuestion")
                    {
                        string? toolId = GetString(b, "id");
                        if (toolId is not null) askUserQuestionToolIds.Add(toolId);
                    }
                }
                continue;
            }

            if (type != "user") continue;
            if (i <= boundary) continue;

            if (TryGetAnswers(e, askUserQuestionToolIds) is { Count: > 0 } answers)
            {
                foreach ((string question, string answer) in answers) said.Add($"{question}: {answer}");
                continue;
            }

            string trimmed = ExtractUserText(e).Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('<') || trimmed.StartsWith("Caveat:", StringComparison.Ordinal)) continue;
            if (ContinuedSummaryStart().IsMatchOrFalse(trimmed)) continue;
            if (HookFeedbackNotice().IsMatchOrFalse(trimmed[..Math.Min(60, trimmed.Length)])) continue;
            said.Add(ConsecutiveWhitespace().ReplaceOrKeep(trimmed, " "));
        }

        for (int i = 0; i < entries.Count; i++)
        {
            if (i <= boundary) continue;
            JsonElement e = entries[i];
            if (e.ValueKind != JsonValueKind.Object || IsNoiseEntry(e)) continue;
            if (GetString(e, "type") != "attachment") continue;
            if (!e.TryGetProperty("attachment", out JsonElement att)) continue;
            if (GetString(att, "type") != "queued_command") continue;
            if (GetString(att, "commandMode") == "task-notification") continue;
            bool isPeer = att.TryGetProperty("origin", out JsonElement origin) && GetString(origin, "kind") == "peer";
            if (isPeer) continue; // a peer's words are not the owner's directive
            string prompt = (GetString(att, "prompt") ?? "").Trim();
            if (prompt.Length > 0) said.Add(prompt);
        }

        return said;
    }

    private static bool IsNoiseEntry(JsonElement e) =>
        (e.TryGetProperty("isSidechain", out JsonElement sc) && sc.ValueKind == JsonValueKind.True) ||
        (e.TryGetProperty("isMeta", out JsonElement meta) && meta.ValueKind == JsonValueKind.True);

    private static int LastCompactionBoundaryIndex(List<JsonElement> entries)
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

    // The verbatim ledger PreCompact writes on every compaction: the full transcript, never cut, never
    // limited to the last few — the brief above is what fits in a budget, this is where the rest lives.
    // Order is transcript order, which is also why re-running this on the same transcript is deterministic.
    private static List<(string timestamp, string who, string text)> LedgerEntries(List<JsonElement> entries)
    {
        List<(string timestamp, string who, string text)> outp = [];
        HashSet<string> seenAssistantText = [];
        HashSet<string> seenQueued = [];
        HashSet<string> askUserQuestionToolIds = [];

        foreach (JsonElement e in entries)
        {
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
                        outp.Add((timestamp, "the owner (answer)", $"{question}: {answer}"));
                    }
                    continue;
                }

                string trimmed = ExtractUserText(e).Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('<') || trimmed.StartsWith("Caveat:", StringComparison.Ordinal)) continue;
                if (ContinuedSummaryStart().IsMatchOrFalse(trimmed)) continue;
                if (HookFeedbackNotice().IsMatchOrFalse(trimmed[..Math.Min(60, trimmed.Length)])) continue;
                outp.Add((timestamp, "the owner", trimmed));
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
                        outp.Add((timestamp, "Arc", text));
                    }
                    else if (btype == "tool_use" && GetString(b, "name") == "AskUserQuestion")
                    {
                        string? toolId = GetString(b, "id");
                        if (toolId is not null) askUserQuestionToolIds.Add(toolId);
                        string question = AskUserQuestionText(b);
                        if (question.Length > 0) outp.Add((timestamp, "Arc (question)", question));
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
                    outp.Add((timestamp, $"Peer {sender}", prompt));
                }
                else
                {
                    outp.Add((timestamp, "the owner (mid-turn)", prompt));
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

    private static string BuildLedger(List<(string timestamp, string who, string text)> ledgerEntries)
    {
        List<string> lines = ["# Compaction ledger — verbatim, never truncated", ""];
        foreach ((string timestamp, string who, string text) in ledgerEntries)
        {
            lines.Add($"- [{timestamp}] {who}: {text}");
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
    [GeneratedRegex(@"\s+", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ConsecutiveWhitespace();
    [GeneratedRegex("from-name=\"([^\"]*)\"", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex FromNameAttribute();
}
