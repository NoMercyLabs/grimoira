using System.Text.Json;
using System.Text.RegularExpressions;
using Aitm.Hooks.Data;
using Microsoft.Data.Sqlite;

namespace Aitm.Hooks.Tools;

/// <summary>
/// PostToolUse handler (Bash|PowerShell), ported from pattern-watch.mjs (RESTRUCTURE.md slice 22,
/// "Hooks, part 3"; docs/RESTRUCTURE.md:268). Notices when the same piece of work keeps being done by
/// hand: it counts command shapes so a repeated pattern becomes visible, without judging wording and
/// without ever blocking. Unlike the .mjs, this handler is record-only — it never prints a nudge into
/// the session (the part the lean pass objected to). What crossed the threshold is read back through
/// <see cref="PatternsTool"/> instead.
///
/// <see cref="Signature"/> is ported verbatim, including its seven fixed bugs (cd swallowing the real
/// command, a redirect read as a subcommand, quotes collapsing a script to its interpreter, the
/// PowerShell spelling of cd, shell keywords, "npm run" naming no task, and codifying things already
/// codified) — pattern-watch.test.mjs pins every one of them.
/// </summary>
public static partial class PatternWatchTool
{
    private const int SeqLen = 3;

    private static readonly HashSet<string> Navigation = new(StringComparer.Ordinal)
        { "cd", "chdir", "set-location", "sl", "pushd", "popd", "push-location", "pop-location" };
    private static readonly HashSet<string> Headers = new(StringComparer.Ordinal)
        { "for", "foreach", "while", "until", "if", "elif", "case", "switch", "select" };
    private static readonly HashSet<string> BodyKeywords = new(StringComparer.Ordinal)
    {
        "do", "then", "else", "done", "fi", "esac", "end", "try", "catch", "finally", "begin", "process",
        "sudo", "time", "env", "nohup", "exec", "command", "call", "export", "set", "source", ".", "function",
        "&&", ";", "|",
    };
    private static readonly HashSet<string> SubcommandLess = new(StringComparer.Ordinal)
        { "node", "python", "python3", "bash", "sh", "pwsh", "powershell" };
    private static readonly HashSet<string> Runners = new(StringComparer.Ordinal)
        { "npm", "yarn", "pnpm", "bun", "npx", "dotnet" };
    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal)
    {
        "git add", "git rm", "git mv", "git status", "git log", "git diff", "git show", "git branch",
        "git checkout", "git switch", "git stash", "git fetch", "git rev-parse", "git ls-files",
        "gh api", "gh auth",
    };
    private static readonly HashSet<string> Utilities = new(StringComparer.Ordinal)
    {
        "grep", "cat", "sed", "awk", "ls", "cd", "echo", "head", "tail", "find", "cut", "sort", "uniq",
        "wc", "tr", "xargs", "cp", "mv", "rm", "mkdir", "touch", "chmod", "curl", "wget", "jq", "tee",
        "python", "python3", "node", "bash", "sh", "pwsh", "powershell", "which", "test", "true", "printf",
        "sleep", "start-sleep", "timeout", "wait", "date", "pwd", "basename", "dirname", "seq", "read",
    };

    public static string? Signature(string raw)
    {
        string? first = (raw ?? "").Split('\n', '\r').FirstOrDefault(l => l.Trim().Length > 0 && !l.Trim().StartsWith('#'));
        if (first is null) return null;

        List<string> words = [];
        foreach (string rawStage in PipelineStageSeparators().Split(first))
        {
            List<string> w = [.. rawStage.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"', '\''))];
            if (w.Count == 0) continue;

            string head = w[0].ToLowerInvariant();
            if (Navigation.Contains(head) || Headers.Contains(head)) continue;

            while (w.Count > 0 && (
                BodyKeywords.Contains(w[0].ToLowerInvariant())
                || w[0].Contains('=')
                || w[0] == "="
                || (w.Count > 1 && w[1] == "=" && VariableAssignmentTarget().IsMatch(w[0]))))
            {
                w = [.. w.Skip(1)];
            }

            if (w.Count == 0 || w[0].StartsWith('-') || w[0].StartsWith('$')) continue;

            int redirect = w.FindIndex(t => ShellRedirect().IsMatch(t));
            words = redirect > 0 ? [.. w.Take(redirect)] : w;
            break;
        }
        if (words.Count == 0) return null;

        string exeToken = words[0].Split('\\', '/').LastOrDefault() ?? words[0];
        if (ScriptFileExtension().IsMatch(exeToken)) return null;
        string exe = WindowsExecutableSuffix().Replace(exeToken, "").ToLowerInvariant();
        if (exe.Length == 0 || exe.Length > 40) return null;
        if (Utilities.Contains(exe) && !Runners.Contains(exe) && !SubcommandLess.Contains(exe)) return null;

        bool IsWord(string w) => CommandWord().IsMatch(w);

        string sub = "";
        if (SubcommandLess.Contains(exe))
        {
            string? script = words.Skip(1).FirstOrDefault(w => !w.StartsWith('-') && IsWord(w.Split('\\', '/').LastOrDefault() ?? ""));
            if (script is not null) sub = (script.Split('\\', '/').LastOrDefault() ?? "").ToLowerInvariant();
        }
        else
        {
            List<string> rest = [.. words.Skip(1)];
            string? found = null;
            for (int i = 0; i < rest.Count; i++)
            {
                string w = rest[i];
                if (!w.StartsWith('-') && !(i > 0 && rest[i - 1].StartsWith('-')) && !w.Contains('/') && !w.Contains('\\') && IsWord(w))
                {
                    found = w;
                    break;
                }
            }
            sub = (found ?? "").ToLowerInvariant();
        }

        if (Runners.Contains(exe) && (sub == "run" || sub == "run-script" || sub == "exec"))
        {
            int idx = words.FindIndex(w => string.Equals(w, sub, StringComparison.OrdinalIgnoreCase));
            string? after = words.Skip(idx + 1).FirstOrDefault(w => !w.StartsWith('-') && IsWord(w));
            sub = after is not null ? $"{sub} {after.ToLowerInvariant()}" : sub;
        }
        if (sub.Length > 40) sub = "";
        if (sub.Length == 0 && Utilities.Contains(exe)) return null;
        string sig = sub.Length > 0 ? $"{exe} {sub}" : exe;
        if (Primitives.Contains(sig)) return null;
        if (ScriptFileExtension().IsMatch(sub)) return null;
        return sig;
    }

    /// <summary>Records the command's signature. Never prints anything and never throws out of a bad
    /// payload or a locked store — a hook must never disturb the command it observed.</summary>
    public static string Execute(string stdin)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string cmd = GetToolInputCommand(payload) ?? "";
            string? sig = Signature(cmd);
            if (sig is null) return "";

            string? cwd = GetString(payload, "cwd");
            string instance = HookPaths.ResolveInstance(cwd);
            string dbPath = HookPaths.DbPath(instance);
            if (!File.Exists(dbPath)) return "";

            using SqliteConnection connection = HookStore.Open(dbPath);
            try
            {
                using SqliteCommand create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE IF NOT EXISTS patterns(" +
                    "sig TEXT PRIMARY KEY, sample TEXT, count INTEGER NOT NULL DEFAULT 1, " +
                    "first_ts TEXT, last_ts TEXT, promoted INTEGER NOT NULL DEFAULT 0)";
                create.ExecuteNonQuery();
            }
            catch (SqliteException) { /* locked or unavailable — fail open */ return ""; }
            try
            {
                using SqliteCommand alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE patterns ADD COLUMN kind TEXT NOT NULL DEFAULT 'command'";
                alter.ExecuteNonQuery();
            }
            catch (SqliteException) { /* column already present */ }

            string ts = DateTime.UtcNow.ToString("o");
            string sample = cmd.Length > 300 ? cmd[..300] : cmd;

            void Bump(string s, string sampleText, string kind)
            {
                using SqliteCommand bump = connection.CreateCommand();
                bump.CommandText =
                    "INSERT INTO patterns(sig, sample, count, first_ts, last_ts, kind) VALUES($sig,$sample,1,$ts,$ts,$kind) " +
                    "ON CONFLICT(sig) DO UPDATE SET count = count + 1, last_ts = excluded.last_ts";
                bump.Parameters.AddWithValue("$sig", s);
                bump.Parameters.AddWithValue("$sample", sampleText);
                bump.Parameters.AddWithValue("$ts", ts);
                bump.Parameters.AddWithValue("$kind", kind);
                bump.ExecuteNonQuery();
            }

            try
            {
                Bump(sig, sample, "command");

                string? sessionId = GetString(payload, "session_id");
                string trailPath = Path.Combine(HookPaths.InstanceDir(instance), "trail", $"{Clip(sessionId ?? "x", 64)}.json");
                List<string> sigs = [];
                List<string> cmds = [];
                try
                {
                    using JsonDocument trailDoc = JsonDocument.Parse(File.ReadAllText(trailPath));
                    if (trailDoc.RootElement.TryGetProperty("sigs", out JsonElement sigsEl) && sigsEl.ValueKind == JsonValueKind.Array)
                        sigs = [.. sigsEl.EnumerateArray().Select(e => e.GetString() ?? "")];
                    if (trailDoc.RootElement.TryGetProperty("cmds", out JsonElement cmdsEl) && cmdsEl.ValueKind == JsonValueKind.Array)
                        cmds = [.. cmdsEl.EnumerateArray().Select(e => e.GetString() ?? "")];
                }
                catch { /* first command */ }

                sigs = [.. sigs, sig];
                if (sigs.Count > SeqLen) sigs = sigs[^SeqLen..];
                cmds = [.. cmds, cmd.Length > 200 ? cmd[..200] : cmd];
                if (cmds.Count > SeqLen) cmds = cmds[^SeqLen..];

                try
                {
                    string trailDir = Path.GetDirectoryName(trailPath)!;
                    Directory.CreateDirectory(trailDir);
                    File.WriteAllText(trailPath, JsonSerializer.Serialize(new { sigs, cmds }));
                }
                catch { /* best effort */ }

                if (sigs.Count == SeqLen && sigs.Distinct().Count() > 1)
                {
                    string seqSig = string.Join(" -> ", sigs);
                    Bump(seqSig, string.Join("\n", cmds), "sequence");
                }
            }
            catch (SqliteException) { /* locked mid-write — fail open */ return ""; }

            return "";
        }
        catch
        {
            // fail open — never disturb a command on a bookkeeping error
            return "";
        }
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n];

    private static string? GetToolInputCommand(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("tool_input", out JsonElement input)
            || input.ValueKind != JsonValueKind.Object) return null;
        return GetString(input, "command");
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    [GeneratedRegex(@"\.(mjs|cjs|js|ts|py|ps1|sh|bat|cmd|rb|pl)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "nl-NL")]
    private static partial Regex ScriptFileExtension();
    [GeneratedRegex(@"^[A-Za-z][\w.:-]*$", RegexOptions.Compiled)]
    private static partial Regex CommandWord();
    [GeneratedRegex(@"^\d?>>?$|^<$|^\d?>&\d$", RegexOptions.Compiled)]
    private static partial Regex ShellRedirect();
    [GeneratedRegex(@"^\$?[\w.]+$", RegexOptions.Compiled)]
    private static partial Regex VariableAssignmentTarget();
    [GeneratedRegex(@"&&|\|\||;|\|")]
    private static partial Regex PipelineStageSeparators();
    [GeneratedRegex(@"\.(exe|cmd)$", RegexOptions.IgnoreCase, "nl-NL")]
    private static partial Regex WindowsExecutableSuffix();
}
