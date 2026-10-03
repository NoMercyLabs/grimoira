using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grimoira.Facts.Tools;
using Grimoira.Hooks.Data;
using Grimoira.Memory.Tools;
using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;
using static Grimoira.Store.Data.JsonShape;

namespace Grimoira.Hooks.Tools;

/// <summary>
/// UserPromptSubmit handler on the server: hands the session the few facts and rules that match the
/// prompt, under a hard budget, so Grimoira is consulted on every prompt without anyone asking it. Says
/// nothing when the prompt is too short to carry terms, is a slash command or tool text, matches nothing,
/// or the instance has no store yet. Fails open everywhere: a prompt must never wait on recall.
/// </summary>
public static partial class PromptRecallTool
{
    /// <summary>The whole additionalContext, in chars (about 150 tokens): a prompt gets a hint, not a page.</summary>
    public const int Budget = 600;

    private const int MinTerms = 3;
    private const int MaxTerms = 10;
    private const string Header = "Grimoira recall for this prompt:";

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
            string facts;
            string rules;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                IUsageSignal usageSignal = new UsageSignal();
                facts = Keep(new QueryTool(usageSignal).ExecuteMcp(connection, query));
                rules = Keep(new MemTool(usageSignal).ExecuteMcp(connection, query));
            }
            if (facts.Length == 0 && rules.Length == 0) return "";

            string context = Clip($"{Header}\n{facts}\n{rules}".TrimEnd());
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
