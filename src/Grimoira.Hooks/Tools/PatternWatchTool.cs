using Grimoira.Store.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grimoira.Hooks.Data;
using Microsoft.Data.Sqlite;
using static Grimoira.Store.Data.JsonShape;

namespace Grimoira.Hooks.Tools;

/// <summary>
/// PostToolUse handler (Bash|PowerShell, and since the takeover Read|Grep|Glob), ported from
/// pattern-watch.mjs (RESTRUCTURE.md slice 22, "Hooks, part 3"; docs/RESTRUCTURE.md:268). Notices when
/// the same piece of work keeps being done by hand: it counts command shapes, and the files and
/// searches a session keeps coming back to, so a repeated pattern becomes visible, without judging
/// wording and without ever blocking. Unlike the .mjs, this handler is record-only — it never prints a nudge into
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
        foreach (string rawStage in PipelineStageSeparators().SplitOrWhole(first))
        {
            List<string> w = [.. rawStage.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"', '\''))];
            if (w.Count == 0) continue;

            string head = w[0].ToLowerInvariant();
            if (Navigation.Contains(head) || Headers.Contains(head)) continue;

            while (w.Count > 0 && (
                BodyKeywords.Contains(w[0].ToLowerInvariant())
                || w[0].Contains('=')
                || w[0] == "="
                || (w.Count > 1 && w[1] == "=" && VariableAssignmentTarget().IsMatchOrFalse(w[0]))))
            {
                w = [.. w.Skip(1)];
            }

            if (w.Count == 0 || w[0].StartsWith('-') || w[0].StartsWith('$')) continue;

            int redirect = w.FindIndex(t => ShellRedirect().IsMatchOrFalse(t));
            words = redirect > 0 ? [.. w.Take(redirect)] : w;
            break;
        }
        if (words.Count == 0) return null;

        string exeToken = words[0].Split('\\', '/').LastOrDefault() ?? words[0];
        if (ScriptFileExtension().IsMatchOrFalse(exeToken)) return null;
        string exe = WindowsExecutableSuffix().ReplaceOrKeep(exeToken, "").ToLowerInvariant();
        if (exe.Length == 0 || exe.Length > 40) return null;
        if (Utilities.Contains(exe) && !Runners.Contains(exe) && !SubcommandLess.Contains(exe)) return null;

        bool IsWord(string w) => CommandWord().IsMatchOrFalse(w);

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
        if (ScriptFileExtension().IsMatchOrFalse(sub)) return null;
        return sig;
    }

    /// <summary>
    /// The signature and kind for one PostToolUse event, picked by <paramref name="toolName"/>: a shell
    /// command keeps <see cref="Signature"/> (kind <c>command</c>); a Read is the file's path relative to
    /// <paramref name="cwd"/> (kind <c>read</c>); a Grep or Glob is its pattern plus the folder it was
    /// scoped to (kinds <c>grep</c>, <c>glob</c>). A repeated search is the shape a fact or a tool can
    /// replace, which is why the read side is counted at all. Any other tool, or an empty input, gives
    /// null: nothing recorded.
    /// </summary>
    public static (string Sig, string Kind)? SignatureFor(string? toolName, JsonElement toolInput, string? cwd)
    {
        if (toolInput.ValueKind != JsonValueKind.Object) return null;
        switch (toolName)
        {
            case "Bash":
            case "PowerShell":
            case null: // a payload without tool_name is the shell slot as the hook ran before tool_name was read
                string? sig = Signature(GetString(toolInput, "command") ?? "");
                return sig is null ? null : (sig, "command");
            case "Read":
                string? file = GetString(toolInput, "file_path");
                return string.IsNullOrWhiteSpace(file) ? null : ($"read {RelativePath(file, cwd)}", "read");
            case "Grep":
            case "Glob":
                string? pattern = GetString(toolInput, "pattern");
                if (string.IsNullOrWhiteSpace(pattern)) return null;
                string kind = toolName == "Grep" ? "grep" : "glob";
                string? path = GetString(toolInput, "path");
                string scope = string.IsNullOrWhiteSpace(path) ? "" : $" in {RelativePath(path, cwd)}";
                return ($"{kind} {pattern.Trim()}{scope}", kind);
            default:
                return null;
        }
    }

    /// <summary>Forward slashes, relative to the session's cwd when the path is under it, and lowercase
    /// on Windows so two spellings of one file land on one row.</summary>
    private static string RelativePath(string path, string? cwd)
    {
        string full = path.Trim();
        try
        {
            full = Path.GetFullPath(full);
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                string root = Path.GetFullPath(cwd);
                string rel = Path.GetRelativePath(root, full);
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) full = rel;
            }
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException) { /* keep the raw text */ }
        full = full.Replace('\\', '/');
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    /// <summary>Records the event's signature. Never prints anything and never throws out of a bad
    /// payload or a locked store — a hook must never disturb the command it observed.</summary>
    public static string Execute(string stdin)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdin) ? "{}" : stdin);
            JsonElement payload = doc.RootElement;
            string? cwd = GetString(payload, "cwd");
            if (!TryGetObjectProperty(payload, "tool_input", out JsonElement toolInput)) return "";
            (string Sig, string Kind)? found = SignatureFor(GetString(payload, "tool_name"), toolInput, cwd);
            if (found is null) return "";
            (string sig, string kind) = found.Value;
            string cmd = kind == "command" ? GetString(toolInput, "command") ?? "" : sig;

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
                Bump(sig, sample, kind);
                // Procedures (a -> b -> c) are a shell notion; a read or a search joins no sequence.
                if (kind != "command") return "";

                string? sessionId = GetString(payload, "session_id");
                string trailPath = Path.Combine(HookPaths.InstanceDir(instance), "trail", $"{Clip(sessionId ?? "x", 64)}.json");
                List<string> sigs = [];
                List<string> cmds = [];
                try
                {
                    using JsonDocument trailDoc = JsonDocument.Parse(File.ReadAllText(trailPath));
                    if (TryGetObjectProperty(trailDoc.RootElement, "sigs", out JsonElement sigsEl) && sigsEl.ValueKind == JsonValueKind.Array)
                        sigs = [.. sigsEl.EnumerateArray().Select(e => e.GetString() ?? "")];
                    if (TryGetObjectProperty(trailDoc.RootElement, "cmds", out JsonElement cmdsEl) && cmdsEl.ValueKind == JsonValueKind.Array)
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

    [GeneratedRegex(@"\.(mjs|cjs|js|ts|py|ps1|sh|bat|cmd|rb|pl)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout.Milliseconds)]
    private static partial Regex ScriptFileExtension();
    [GeneratedRegex(@"^[A-Za-z][\w.:-]*$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex CommandWord();
    [GeneratedRegex(@"^\d?>>?$|^<$|^\d?>&\d$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex ShellRedirect();
    [GeneratedRegex(@"^\$?[\w.]+$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex VariableAssignmentTarget();
    [GeneratedRegex(@"&&|\|\||;|\|", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PipelineStageSeparators();
    [GeneratedRegex(@"\.(exe|cmd)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout.Milliseconds)]
    private static partial Regex WindowsExecutableSuffix();
}
