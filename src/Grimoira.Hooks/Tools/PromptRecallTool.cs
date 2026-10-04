using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grimoira.Docs.Tools;
using Grimoira.Facts.Tools;
using Grimoira.Hooks.Data;
using Grimoira.Memory.Tools;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using static Grimoira.Store.Data.JsonShape;

namespace Grimoira.Hooks.Tools;

/// <summary>
/// UserPromptSubmit handler on the server: hands the session the top docs, facts and rules that match the
/// prompt, as one line each under a hard budget, so Grimoira is the first source on every prompt without
/// anyone asking it. The searches are the MCP <c>doc</c>, <c>fact</c> and <c>rule</c> tools themselves
/// (<see cref="DocTool.ExecuteMcp"/>, <see cref="QueryTool.ExecuteMcp"/>, <see cref="MemTool.ExecuteMcp"/>),
/// so the relevance gates and gap logging stay in one place; this only reshapes their bullets. Says nothing
/// when the prompt is too short to carry terms, is a slash command or tool text, matches nothing, or the
/// instance has no store yet. Fails open everywhere: a prompt must never wait on recall.
/// </summary>
public static partial class PromptRecallTool
{
    /// <summary>The whole additionalContext, in chars (about 300 tokens): a prompt gets a hint, not a page.</summary>
    public const int Budget = 1200;

    /// <summary>Hits over all channels together: one line each.</summary>
    public const int MaxHits = 3;

    private const int MinTerms = 3;
    private const int MaxTerms = 10;
    private const int HitTextChars = 200;
    private const string Header = "Grimoira (top hits for this prompt; cite or open them before reading other files):";

    // Words that carry no subject. Short on purpose: the FTS tools drop their own stop words too.
    private static readonly HashSet<string> Stop =
    [
        "the", "and", "for", "with", "this", "that", "from", "into", "you", "are", "can", "please", "make",
        "file", "code", "run", "use", "does", "why", "what", "how", "where", "when", "which", "not", "but",
        "all", "any", "add", "get", "set", "let", "its", "our", "your", "have", "has", "was", "were", "will",
        "should", "could", "would", "then", "than", "also", "just", "now", "here", "there", "one", "two",
    ];

    public static string Execute(string stdin, string? projectDir)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string prompt = (GetString(payload, "prompt") ?? "").Trim();
            if (prompt.Length == 0 || prompt[0] is '/' or '<') return "";

            List<string> terms = Terms(prompt);
            if (terms.Count < MinTerms) return "";

            string instance = HookPaths.ResolveInstance(GetString(payload, "cwd"), projectDir);
            if (GateSwitch.IsPaused(instance)) return "";
            string dbPath = HookPaths.DbPath(instance);
            if (!File.Exists(dbPath)) return "";

            string query = string.Join(' ', terms);
            List<string> docs;
            List<string> facts;
            List<string> rules;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                IUsageSignal usageSignal = new UsageSignal();
                docs = Hits("doc", Keep(new DocTool().ExecuteMcp(connection, query)));
                facts = Hits("fact", Keep(new QueryTool(usageSignal).ExecuteMcp(connection, query)));
                rules = Hits("memory", Keep(new MemTool(usageSignal).ExecuteMcp(connection, query)));
            }
            List<string> hits = TopHits(docs, facts, rules);
            if (hits.Count == 0) return "";

            string context = Clip($"{Header}\n{string.Join('\n', hits)}");
            Log(instance, GetString(payload, "session_id") ?? "", terms.Count, context.Length);

            var envelope = new
            {
                hookSpecificOutput = new { hookEventName = "UserPromptSubmit", additionalContext = context },
            };
            return JsonSerializer.Serialize(envelope);
        }
        catch
        {
            // fail open
            return "";
        }
    }

    /// <summary>Lowercase words of three letters or more, minus the stop list, first ten, in prompt order.</summary>
    private static List<string> Terms(string prompt) =>
        [.. WordPattern().MatchesOrEmpty(prompt.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && !Stop.Contains(w))
            .Distinct()
            .Take(MaxTerms)];

    // A "no match" answer from either tool is not recall; only real hits go to the session.
    private static string Keep(string answer) =>
        answer.StartsWith("no ", StringComparison.Ordinal) || answer.Contains("not indexed yet", StringComparison.Ordinal)
            ? ""
            : answer.TrimEnd();

    /// <summary>One hit per channel in turn (doc, fact, rule, doc, ...) until <see cref="MaxHits"/>, so no channel crowds the others out.</summary>
    private static List<string> TopHits(params List<string>[] channels)
    {
        List<string> hits = [];
        for (int i = 0; hits.Count < MaxHits; i++)
        {
            bool any = false;
            foreach (List<string> channel in channels)
            {
                if (i >= channel.Count) continue;
                any = true;
                if (hits.Count < MaxHits) hits.Add(channel[i]);
            }
            if (!any) break;
        }
        return hits;
    }

    /// <summary>
    /// Reshapes one MCP answer into one line per hit: <c>[channel] title (path) — text</c>. The bullets are
    /// <c>• head</c> then indented lines; the head carries the title (and for docs the path), the first
    /// indented line the text, and a fact's <c>source:</c> line its path. Anything else (the budget trailer)
    /// is skipped.
    /// </summary>
    private static List<string> Hits(string channel, string answer)
    {
        List<string> hits = [];
        if (answer.Length == 0) return hits;
        string? head = null;
        string text = "";
        string path = "";
        foreach (string raw in answer.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("• ", StringComparison.Ordinal))
            {
                Flush();
                head = line[2..];
            }
            else if (head is not null)
            {
                // A doc's content keeps its own line breaks; every line up to the next bullet is its text.
                string body = line.Trim();
                if (body.Length == 0 || body.StartsWith("(+", StringComparison.Ordinal)) continue;
                if (body.StartsWith("source: ", StringComparison.Ordinal)) path = body[8..].TrimEnd('…');
                else text = text.Length == 0 ? body : $"{text} {body}";
            }
        }
        Flush();
        return hits;

        void Flush()
        {
            if (head is null) return;
            (string title, string headPath) = SplitHead(channel, head);
            if (headPath.Length > 0) path = headPath;
            string where = path.Length > 0 ? $" ({path})" : "";
            // An indexed doc section starts with its own title; the line already carries it.
            if (text.StartsWith(title, StringComparison.Ordinal)) text = text[title.Length..].TrimStart();
            hits.Add($"[{channel}] {title}{where} — {ClipText(text.Length > 0 ? text : title)}");
            head = null;
            text = "";
            path = "";
        }
    }

    // doc: "[category] title (path)"; fact: "term [category]"; memory: "[HARD type] title".
    private static (string title, string path) SplitHead(string channel, string head)
    {
        switch (channel)
        {
            case "doc":
            {
                int close = head.IndexOf("] ", StringComparison.Ordinal);
                string rest = close >= 0 ? head[(close + 2)..] : head;
                int open = rest.LastIndexOf(" (", StringComparison.Ordinal);
                return open > 0 && rest.EndsWith(')')
                    ? (rest[..open], rest[(open + 2)..^1].TrimEnd('…'))
                    : (rest, "");
            }
            case "fact":
            {
                int open = head.LastIndexOf(" [", StringComparison.Ordinal);
                return (open > 0 ? head[..open] : head, "");
            }
            default:
            {
                int close = head.IndexOf("] ", StringComparison.Ordinal);
                return (close >= 0 ? head[(close + 2)..] : head, "");
            }
        }
    }

    private static string ClipText(string text) =>
        text.Length <= HitTextChars ? text : text[..HitTextChars].TrimEnd() + "…";

    /// <summary>Cuts at the last full line that fits the budget, marked with "…"; never mid-line.</summary>
    private static string Clip(string text)
    {
        if (text.Length <= Budget) return text;
        const string marker = "…";
        int limit = Budget - marker.Length;
        int cut = text.LastIndexOf('\n', limit);
        if (cut <= 0) cut = limit;
        return text[..cut].TrimEnd() + marker;
    }

    // The live-proof line: one per non-empty recall, so a real session shows the hook fired and how much it
    // sent. Best effort.
    private static void Log(string instance, string sessionId, int terms, int chars)
    {
        try
        {
            string dir = HookPaths.InstanceDir(instance);
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "recall.log"),
                $"{DateTime.UtcNow:o} UserPromptSubmit {sessionId} terms={terms} chars={chars}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // best effort
        }
    }

    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant, RegexTimeout.Milliseconds)]
    private static partial Regex WordPattern();
}
