#:package ModelContextProtocol@1.3.0
#:package Microsoft.Extensions.Hosting@9.0.0
#:package Microsoft.Data.Sqlite@10.0.9
// Pinned to the safe floor: Microsoft.Data.Sqlite otherwise resolves SQLitePCLRaw 2.1.10, which
// carries GHSA-2m69-gcr7-jv3q. Keep in lockstep with aitm.cs.
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3
#:project src/Aitm.Store/Aitm.Store.csproj
#:project src/Aitm.Facts/Aitm.Facts.csproj
#:project src/Aitm.Memory/Aitm.Memory.csproj
#:project src/Aitm.Docs/Aitm.Docs.csproj
// AITM MCP server: exposes the per-instance store (knowledge, the cross-project impact graph,
// history, findings) as tools the agent calls every session. stdio transport; all host logging
// disabled so only tool output reaches the client.
// Instance resolves from AITM_INSTANCE, else the project dir (CLAUDE_PROJECT_DIR or cwd) basename,
// so the one user-scope server serves whatever repo the session runs in; store at ~/.aitm/<instance>/aitm.db.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Aitm.Store.Data;
using Aitm.Store.Tools;
using Aitm.Facts.Tools;
using Aitm.Memory.Tools;
using Aitm.Docs.Tools;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

[McpServerToolType]
public static partial class AitmTools
{
    [GeneratedRegex("^[a-z0-9_-]{2,30}$")]
    private static partial Regex KindToken();

    private static readonly string[] NodeKinds =
    {
        "rule", "fact", "concept", "symbol", "finding", "contract",
        "seam", "codekind", "project", "reference", "platform", "layer",
    };

    // The ROW types, which is what brain_stage's first argument actually selects. Disjoint from
    // NodeKinds by construction, which is what makes it safe to accept a node kind there and coerce.
    private static readonly string[] RowKinds = { "node", "triple", "slot" };

    // A rejection has to be actionable on the FIRST try. Returning only the rule that was broken made
    // callers guess the positional meaning of key/a/b/c and burn five round trips in a row, and the
    // knowledge they were trying to record never landed at all.
    private static string Usage(string offending) =>
        $"rejected: {offending}\n" +
        "kind is the ROW TYPE, one of node | triple | slot. It is NOT the node's own kind.\n" +
        "  node:   kind=\"node\"   key=<stable-id>  a=<node kind>  b=<short label>  c=<full statement>\n" +
        "  triple: kind=\"triple\" key=<subject>    a=<predicate>  b=<object>      because=<why>\n" +
        "  slot:   kind=\"slot\"   key=<frame>      a=<name>       b=<value>\n" +
        $"node kinds: {string.Join(" / ", NodeKinds)}\n" +
        "example: kind=\"node\" key=\"nvenc-windows-native\" a=\"fact\" " +
        "b=\"NVENC encodes natively on Windows only\" c=\"Confirmed on real hardware: 3.03x, exit 0, 345 KB output. Never through WSL.\"";

    // Write-side shape guard: 85 nodes got written with scrambled positional args (paragraph in
    // label/kind, title in kind). Reject the shape at the door so the caller re-orders instead of
    // poisoning recall ranking.
    private static string NodeGuard(string kind, string label)
    {
        if (!KindToken().IsMatch(kind))
            return Usage($"\"{Trim(kind)}\" is not a node kind — it looks like prose, and a=<node kind> is a single short token.");
        if (label.Length > 120)
            return Usage($"label is {label.Length} chars; it must be a short noun phrase (max 120). The full statement goes in c.");
        return "";
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..60] + "…";

    // The commonest miss by far: passing the node's own kind ("fact", "rule") as the row type. Naming
    // that specific mistake fixes the call in one round trip instead of restating the rule that broke.
    private static string WrongRowKind(string kind) =>
        NodeKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)
            ? Usage($"you passed \"{kind}\" as kind, but that is a NODE kind. Use kind=\"node\" and move \"{kind}\" into a.")
            : Usage($"\"{Trim(kind)}\" is not a row type.");

    // Output discipline: every tool answer is context the caller pays for. Cells clip, payloads clip,
    // and the whole answer stays under OutCap (brain_core gets CoreCap — it is the once-per-session
    // always-on set and must not lose hard rules to trimming).
    private const int CellCap = 160;
    private const int OutCap = 1800;
    private const int CoreCap = 6000;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are",
        "does", "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you",
        "we", "there", "was", "were", "which", "when", "where", "name", "called", "get", "got",
    };

    private static SqliteConnection Open()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm", ResolveInstance(), "aitm.db");
        SqliteConnection con = new($"Data Source={path};Mode=ReadWriteCreate;Foreign Keys=True");
        con.Open();
        using (SqliteCommand busy = con.CreateCommand())
        {
            // Matches aitm.cs: a momentary writer must block this connection, not kill it, and WAL
            // keeps readers running while the long doc/chat indexers hold the write lock.
            busy.CommandText = "PRAGMA busy_timeout=30000; PRAGMA journal_mode=WAL;";
            busy.ExecuteNonQuery();
        }
        using SqliteCommand init = con.CreateCommand();
        init.CommandText = """
            CREATE TABLE IF NOT EXISTS usage (node_k TEXT PRIMARY KEY, hits INTEGER NOT NULL DEFAULT 0, last_used TEXT, verified_at TEXT);
            CREATE TABLE IF NOT EXISTS gaps (id INTEGER PRIMARY KEY, query TEXT NOT NULL UNIQUE, tool TEXT NOT NULL,
              misses INTEGER NOT NULL DEFAULT 1, first_ts TEXT NOT NULL, last_ts TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'open');
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            """;
        init.ExecuteNonQuery();
        MaybeMaintain(con);
        return con;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    // Hard ceiling on a whole tool answer; trims at a line boundary and says so, so a flooded
    // result reads as "narrow the query", never as "that was everything".
    private static string Budget(string s, int cap = OutCap)
    {
        if (s.Length <= cap) return s;
        int cut = s.LastIndexOf('\n', cap);
        if (cut < cap / 2) cut = cap;
        int dropped = s[cut..].Count(ch => ch == '\n');
        return s[..cut] + $"\n(+{dropped} more line(s) trimmed — narrow the query)";
    }

    // Every refused/empty lookup is recorded: the brain learns what it does NOT know. brain_gaps lists
    // them; a later brain_learn whose text covers the gap's tokens auto-resolves it.
    private static string LogGap(SqliteConnection con, string tool, string query)
    {
        try
        {
            string norm = string.Join(' ', Tokens(query));
            if (norm.Length < 3) return "";
            using SqliteCommand g = con.CreateCommand();
            g.CommandText = @"INSERT INTO gaps(query,tool,misses,first_ts,last_ts)
                VALUES($q,$t,1,strftime('%Y-%m-%dT%H:%M:%fZ','now'),strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                ON CONFLICT(query) DO UPDATE SET misses=misses+1, last_ts=strftime('%Y-%m-%dT%H:%M:%fZ','now'), status='open', tool=$t";
            g.Parameters.AddWithValue("$q", norm);
            g.Parameters.AddWithValue("$t", tool);
            g.ExecuteNonQuery();
            return " [gap logged — stage the answer via brain_stage once you learn it]";
        }
        catch (SqliteException)
        {
            return "";
        }
    }

    private static void ResolveGaps(SqliteConnection con, string learnedText)
    {
        try
        {
            string hay = learnedText.ToLowerInvariant();
            List<long> filled = new();
            using (SqliteCommand sel = con.CreateCommand())
            {
                sel.CommandText = "SELECT id, query FROM gaps WHERE status='open' LIMIT 200";
                using SqliteDataReader r = sel.ExecuteReader();
                while (r.Read())
                {
                    if (r.GetString(1).Split(' ').All(t => hay.Contains(t, StringComparison.Ordinal))) filled.Add(r.GetInt64(0));
                }
            }
            foreach (long id in filled)
            {
                using SqliteCommand u = con.CreateCommand();
                u.CommandText = "UPDATE gaps SET status='filled', last_ts=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=$i";
                u.Parameters.AddWithValue("$i", id);
                u.ExecuteNonQuery();
            }
        }
        catch (SqliteException)
        {
        }
    }

    // Self-maintenance, time-gated to every 3+ days, piggybacked on a normal open: backfill missing
    // schemes, prune stale/overflow gaps, keep the FTS index tight. Failure never breaks the read.
    private static void MaybeMaintain(SqliteConnection con)
    {
        try
        {
            using SqliteCommand get = con.CreateCommand();
            get.CommandText = "SELECT value FROM meta WHERE key='last_maint'";
            if (get.ExecuteScalar() is string last
                && DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime ts)
                && (DateTime.UtcNow - ts).TotalDays < 3) return;
            using SqliteCommand m = con.CreateCommand();
            m.CommandText = """
                UPDATE node SET scheme=kind WHERE valid_to IS NULL AND (scheme IS NULL OR scheme='');
                DELETE FROM gaps WHERE status!='open' AND last_ts < strftime('%Y-%m-%dT%H:%M:%fZ','now','-30 days');
                DELETE FROM gaps WHERE id NOT IN (SELECT id FROM gaps ORDER BY misses DESC, last_ts DESC LIMIT 200);
                INSERT INTO node_fts(node_fts) VALUES('optimize');
                INSERT OR REPLACE INTO meta(key,value) VALUES('last_maint', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            m.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    private static string ResolveInstance()
    {
        string? env = Environment.GetEnvironmentVariable("AITM_INSTANCE");
        if (!string.IsNullOrWhiteSpace(env)) return Slug(env);
        string? proj = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR");
        string dir = string.IsNullOrWhiteSpace(proj) ? Directory.GetCurrentDirectory() : proj;
        string name = Path.GetFileName(dir.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? "default" : Slug(name);
    }

    private static string Slug(string text) => new(text.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());

    private static long Count(SqliteConnection con, string sql)
    {
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static List<string> Tokens(string terms) =>
        terms.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 1 && !Stop.Contains(t))
            .ToList();

    private static string Match(string terms) => string.Join(" OR ", Tokens(terms).Select(t => $"\"{t}\""));

    [McpServerTool]
    [Description("READ-ONLY look-up of a VERIFIED project fact (real base URL, file path, type/field name, config key, API route, port, convention) from the ground-truth store. Returns the top matches with source. Refuses if not in the knowledge base — never guesses; every refusal is auto-logged as a gap (see brain_gaps). Use this before emitting any project-specific fact. To RECORD a new fact, do NOT use this — stage it with brain_stage then commit with brain_flush.")]
    public static string fact(string query)
    {
        using SqliteConnection con = Open();
        return new QueryTool(new UsageSignal()).ExecuteMcp(con, query);
    }

    [McpServerTool]
    [Description("Given a server symbol/field/contract that is CHANGING (a renamed JSON field, endpoint, or SignalR event), list every cross-project consumer site that references it and flag the hardcoded ones that break on a rename. Call BEFORE changing any shared contract.")]
    public static string impact(string symbol)
    {
        using SqliteConnection con = Open();
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = "SELECT project,file,line,usage,hardcoded,contract FROM edges WHERE symbol=$s COLLATE NOCASE ORDER BY hardcoded DESC, project, file";
        cmd.Parameters.AddWithValue("$s", symbol);
        using SqliteDataReader r = cmd.ExecuteReader();
        StringBuilder sb = new();
        int hard = 0;
        string contract = "";
        while (r.Read())
        {
            bool h = r.GetInt32(4) == 1;
            if (h) hard++;
            contract = r.GetString(5);
            sb.AppendLine($"  {r.GetString(0)} {r.GetString(1)}:{r.GetInt32(2)} {(h ? "[HARDCODED] " : "")}{r.GetString(3)}");
        }
        if (sb.Length == 0) return $"'{symbol}' has no recorded consumers (safe to change, or not yet indexed).";
        return $"Changing '{symbol}' ({contract}) impacts these consumers ({hard} hardcoded — break on rename; surface for lockstep, never break existing users):\n{sb}";
    }

    [McpServerTool]
    [Description("Find existing NoMercy tools by purpose, such as browser login, native code search, CI watching, or process ownership. Returns a few source paths with reviewed prerequisites and limitations when available. Read-only discovery: does not execute the found tools or access credentials. Coverage is explicit; a missing result is not proof that no tool exists.")]
    public static async Task<string> workspace_capabilities(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1000)
            return "Provide a task description between 1 and 1000 characters.";
        return await RunWorkspacePython("workspace-capabilities.py", "Workspace lookup", 25,
            "--limit", "3", "--", query);
    }

    [McpServerTool]
    [Description("Search fixed text or filenames in one registered NoMercy repository. Requires an explicit repository path from the registry; '.' means the root. Returns file and line locations only, excludes common credential paths, and reports limits as incomplete coverage. Set names to true for filenames and path to narrow within the repository.")]
    public static async Task<string> workspace_search(string repository, string pattern, string path = "", bool names = false)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 200 ||
            string.IsNullOrWhiteSpace(pattern) || pattern.Length > 200 || path.Length > 500)
            return "Provide a registered repository and fixed text pattern of at most 200 characters.";
        List<string> args = ["--repo", repository, "--pattern", pattern, "--max-results", "15", "--timeout", "10"];
        if (names) args.Add("--names");
        if (!string.IsNullOrWhiteSpace(path)) { args.Add("--path"); args.Add(path); }
        return await RunWorkspacePython("workspace-search.py", "Workspace search", 20, args.ToArray());
    }

    private static async Task<string> RunWorkspacePython(string fileName, string operation, int seconds, params string[] arguments)
    {
        string root = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? Directory.GetCurrentDirectory();
        string script = Path.Combine(root, "scripts", fileName);
        if (!File.Exists(script)) return $"{operation} unavailable: set CLAUDE_PROJECT_DIR to the NoMercy workspace root.";
        ProcessStartInfo start = new()
        {
            FileName = Environment.GetEnvironmentVariable("AITM_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3"),
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(script);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using Process process = Process.Start(start)!;
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(seconds));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { return $"{operation} timed out; child cleanup is unverified."; }
                return $"{operation} timed out; coverage is incomplete. Narrow the task and retry.";
            }
            string output = await stdout;
            await stderr; // Drain both pipes; do not return raw subprocess diagnostics.
            if (output.Length > 12000) output = output[..12000] + "\nOutput truncated; coverage incomplete.";
            return process.ExitCode == 0 ? output : $"{operation} incomplete (exit {process.ExitCode}). Narrow the task or inspect the named repository.\n{output}";
        }
        catch (Exception)
        {
            return $"Could not start {operation.ToLowerInvariant()}. Check Python availability or configure AITM_PYTHON.";
        }
    }

    // Locate the token-exchange engine (idp-impersonate.mjs) that ships alongside this source. The
    // compiled dll runs from bin/, so walk up from the assembly directory until the file appears; an
    // explicit AITM_HOME overrides. One origin for the exchange — the tool never reimplements it.
    private static string? EnginePath()
    {
        string? home = Environment.GetEnvironmentVariable("AITM_HOME");
        if (!string.IsNullOrEmpty(home))
        {
            string p = Path.Combine(home, "idp-impersonate.mjs");
            return File.Exists(p) ? p : null;
        }
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string p = Path.Combine(dir, "idp-impersonate.mjs");
            if (File.Exists(p)) return p;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    [McpServerTool]
    [Description("INTERNAL TESTING. Get a test user token — use this whenever automated work needs a real user token (for an API/SignalR/test call) or you are blocked by a login screen. Mints a real IdP access token for a subject (user GUID, email, or username) via the supported token-exchange grant, with audience=nomercy-server so the media-server accepts it. Defaults to dev; pass realm=\"prod\". This is the ONLY sanctioned way to authenticate for automated work — never weaken/bypass auth or scrape a live session. To LOG A CLIENT IN (not just get a raw token) there are sibling scripts in the same aitm folder: idp-login-web.mjs (browser), idp-login-kmp.mjs (phone), idp-approve-device.mjs (TV); full guide in docs/test-login-and-token-exchange.md. GATED: this tool no-ops unless AITM_ALLOW_TOKEN_MINT=1 (an always-on impersonation primitive is a large blast radius) — when gated, run the script directly instead. Never fabricates a user. Reads the nomercy-api secret from env or nomercy-tv/.env.")]
    public static string idp_token(string subject, string realm = "dev")
    {
        if (Environment.GetEnvironmentVariable("AITM_ALLOW_TOKEN_MINT") != "1")
        {
            return "refused: token minting is gated. This tool impersonates a real user, so it is off by "
                + "default. Set AITM_ALLOW_TOKEN_MINT=1 to enable it here, or run the script directly: "
                + "node idp-impersonate.mjs <subject> [--prod]. To LOG A CLIENT IN instead of getting a "
                + "raw token, use the sibling drivers in the aitm folder — idp-login-web.mjs (browser), "
                + "idp-login-kmp.mjs (phone), idp-approve-device.mjs (TV); see "
                + "docs/test-login-and-token-exchange.md.";
        }
        if (string.IsNullOrWhiteSpace(subject)) return "subject is required (a user GUID, email, or username).";
        realm = realm?.Trim().ToLowerInvariant() switch { "prod" => "prod", "" or null => "dev", "dev" => "dev", var r => r! };
        if (realm != "dev" && realm != "prod") return $"unknown realm \"{realm}\" — use \"dev\" or \"prod\".";

        string? engine = EnginePath();
        if (engine == null) return "cannot locate idp-impersonate.mjs — set AITM_HOME to the aitm checkout directory.";

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "node",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(engine);
            psi.ArgumentList.Add(subject);
            if (realm == "prod") psi.ArgumentList.Add("--prod");

            using Process proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            if (proc.ExitCode != 0)
            {
                return $"token-exchange failed:\n{(stderr.Length > 0 ? stderr : stdout)}".Trim();
            }
            // The engine already decodes the token and reports whether the audience matches; pass its
            // JSON straight through so the caller sees sub / preferred_username / aud / expiry, not just
            // a raw token they would have to trust blind.
            return (stderr.Length > 0 ? stderr + "\n" : "") + stdout.Trim();
        }
        catch (Exception ex)
        {
            return $"could not run the token-exchange engine: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Recall what was said in PAST conversations/sessions (the chat-history channel, kept separate from verified facts). Use when you need something the operator said earlier or many sessions ago, before repeating yourself or re-asking.")]
    public static string recall(string query)
    {
        using SqliteConnection con = Open();
        return new RecallTool().ExecuteMcp(con, query);
    }

    [McpServerTool]
    [Description("Search the absorbed AI-meta docs (PRDs, plans, specs, audits) by topic. These were moved out of the repo into the foundation, chunked by section. Use to recall a requirement, design decision, or plan instead of grepping files.")]
    public static string doc(string query)
    {
        using SqliteConnection con = Open();
        return new DocTool().ExecuteMcp(con, query);
    }

    [McpServerTool]
    [Description("READ-ONLY recall of a standing RULE, preference, or past decision about HOW to work on this project — the migrated memory channel (feedback/user/project/reference), relevance-ranked. Use before writing code or making a call where the operator has likely already set a convention, instead of guessing or repeating a past correction. To CREATE or UPDATE a rule, do NOT use this — stage it with brain_stage then commit with brain_flush.")]
    public static string rule(string query)
    {
        using SqliteConnection con = Open();
        return new MemTool(new UsageSignal()).ExecuteMcp(con, query);
    }

    [McpServerTool]
    [Description("Forget one memory (a migrated RULE/preference/decision) by its exact key — deletes the row from the memory channel and logs the deletion to the cold trail. Use to retire a rule that no longer holds. The key is the slug shown by aitm's memory tooling; a wrong key is a no-op.")]
    public static string shed_memory(string key)
    {
        using SqliteConnection con = Open();
        return new ShedMemoryTool().ExecuteMcp(con, key);
    }

    // --- BRAIN graph: a structured copy of the operator's mental model. Each read is one self-contained
    //     statement; the write-last path supersedes (never deletes) and is trigger-guarded. ---

    private static List<string> SplitArgs(string s) =>
        s.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string NormProj(SqliteConnection con, string input)
    {
        string t = input.Trim();
        if (t.Contains(':')) return t;
        using SqliteCommand c = con.CreateCommand();
        c.CommandText = "SELECT k FROM proj_alias WHERE short=$s";
        c.Parameters.AddWithValue("$s", t);
        return c.ExecuteScalar() is string k ? k : "proj:" + t;
    }

    // One result line per row: empty cells dropped, a cell that merely repeats or prefixes its
    // neighbour dropped (k == label, label == gloss-head are the common cases), every cell clipped.
    // Prefix-dedupe only kicks in past 3 chars so short numeric cells (flags, counts) survive.
    private static string RowLine(SqliteDataReader r)
    {
        List<string> raw = new();
        for (int i = 0; i < r.FieldCount; i++)
        {
            string v = r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            if (v.Length > 0) raw.Add(v);
        }
        List<string> kept = new();
        for (int i = 0; i < raw.Count; i++)
        {
            string v = raw[i];
            bool nextExtends = i + 1 < raw.Count && v.Length > 3 && raw[i + 1].StartsWith(v, StringComparison.OrdinalIgnoreCase);
            bool prevCovers = kept.Count > 0 && (string.Equals(kept[^1], v, StringComparison.OrdinalIgnoreCase)
                || (v.Length > 3 && kept[^1].StartsWith(v, StringComparison.OrdinalIgnoreCase))
                || kept[^1].EndsWith(":" + v, StringComparison.OrdinalIgnoreCase));
            if (nextExtends || prevCovers) continue;
            kept.Add(v);
        }
        return kept.Count > 0 ? "• " + string.Join("  |  ", kept.Select(v => Clip(v, CellCap))) : "";
    }

    private static string Rows(SqliteCommand cmd, int cap = OutCap)
    {
        using SqliteDataReader r = cmd.ExecuteReader();
        StringBuilder sb = new();
        while (r.Read())
        {
            string line = RowLine(r);
            if (line.Length > 0) sb.AppendLine(line);
        }
        return sb.Length == 0 ? "(nothing)" : Budget(sb.ToString(), cap);
    }

    // Like Rows, but also reinforces each row's first column (the surfaced node key). The reader is disposed
    // before the write, so reinforcement never races the open reader on the single connection. This is the
    // automatic half of "smarter with use" on the path the agent actually calls.
    private static string RowsK(SqliteConnection con, SqliteCommand cmd)
    {
        StringBuilder sb = new();
        List<string> keys = new();
        using (SqliteDataReader r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (!r.IsDBNull(0))
                {
                    string k0 = Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture) ?? "";
                    if (k0.Length > 0) keys.Add(k0);
                }
                string line = RowLine(r);
                if (line.Length > 0) sb.AppendLine(line);
            }
        }
        Reinforce(con, keys);
        return sb.Length == 0 ? "(nothing)" : Budget(sb.ToString());
    }

    // Bump hits + recency for surfaced nodes. Guarded on live-node existence so non-node first columns
    // (slot names, project shorthands) never create junk usage rows.
    private static void Reinforce(SqliteConnection con, IEnumerable<string> keys)
    {
        foreach (string k in keys.Where(s => s.Length > 0).Distinct())
        {
            using SqliteCommand u = con.CreateCommand();
            u.CommandText = "INSERT INTO usage(node_k,hits,last_used) SELECT $k,1,strftime('%Y-%m-%dT%H:%M:%fZ','now') " +
                "WHERE EXISTS(SELECT 1 FROM node_now WHERE k=$k) " +
                "ON CONFLICT(node_k) DO UPDATE SET hits=hits+1, last_used=strftime('%Y-%m-%dT%H:%M:%fZ','now')";
            u.Parameters.AddWithValue("$k", k);
            u.ExecuteNonQuery();
        }
    }

    [McpServerTool]
    [Description("BRAIN/always-on: the hard=1 nodes that apply every turn (the ~20-row replacement for the always-loaded MEMORY.md). Pull at the START of a session/turn before doing project work. No args.")]
    public static string brain_core()
    {
        using SqliteConnection con = Open();
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = "SELECT k, label, gloss FROM node_now WHERE hard = 1 ORDER BY scheme, kind, k";
        return Rows(cmd, CoreCap);
    }

    [McpServerTool]
    [Description("BRAIN/scope: everything in scope for working across the named projects — shared seams/contracts/rules floated to top with DIRECTION (who exposes vs who consumes), each side's specifics below. One query. Use before any cross-project task. Args: projects = space/comma-separated keys or short aliases (web, android, server, ios).")]
    public static string brain_scope(string projects)
    {
        using SqliteConnection con = Open();
        List<string> keys = SplitArgs(projects).Select(p => NormProj(con, p)).ToList();
        if (keys.Count < 1) return "give 1+ project (e.g. 'server web').";
        string values = string.Join(",", keys.Select((_, i) => $"($p{i})"));
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = $@"WITH targets(k) AS (VALUES {values})
            SELECT t.o AS topic, n.label, n.gloss, n.scheme, n.hard,
                   COUNT(DISTINCT t.s) AS scope,
                   substr(group_concat(DISTINCT t.s||':'||t.p),1,120) AS who_and_how
            FROM triple_now t JOIN targets g ON g.k = t.s JOIN node_now n ON n.k = t.o
            WHERE t.p IN (SELECT p FROM pred_vocab WHERE is_sharing=1) AND t.o_is_literal = 0
            GROUP BY t.o ORDER BY n.hard DESC, scope DESC, SUM(t.conf) DESC, n.scheme LIMIT 24";
        for (int i = 0; i < keys.Count; i++) cmd.Parameters.AddWithValue($"$p{i}", keys[i]);
        return RowsK(con, cmd);
    }

    [McpServerTool]
    [Description("BRAIN/intersection: what the named projects have in COMMON. Coverage-tolerant — returns shared_by + present_n + an unresolved list, so near-misses surface and a typo'd project is echoed, not silently dropped. One query. Args: projects = 2+ keys/aliases.")]
    public static string brain_common(string projects)
    {
        using SqliteConnection con = Open();
        List<string> keys = SplitArgs(projects).Select(p => NormProj(con, p)).ToList();
        if (keys.Count < 2) return "give 2+ projects (e.g. 'web android ios').";
        string values = string.Join(",", keys.Select((_, i) => $"($p{i})"));
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = $@"WITH req(name) AS (VALUES {values}),
            present AS (SELECT DISTINCT n.k FROM req JOIN node_now n ON n.k = req.name)
            SELECT t.o AS shared, n.label, n.gloss,
                   COUNT(DISTINCT t.s) AS shared_by, (SELECT COUNT(*) FROM present) AS present_n,
                   (SELECT group_concat(name) FROM req WHERE name NOT IN (SELECT k FROM present)) AS unresolved
            FROM triple_now t JOIN present pr ON pr.k = t.s JOIN node_now n ON n.k = t.o
            WHERE t.p IN (SELECT p FROM pred_vocab WHERE is_sharing=1) AND t.o_is_literal = 0
            GROUP BY t.o HAVING COUNT(DISTINCT t.s) >= 2 ORDER BY shared_by DESC, n.scheme, n.label LIMIT 24";
        for (int i = 0; i < keys.Count; i++) cmd.Parameters.AddWithValue($"$p{i}", keys[i]);
        return RowsK(con, cmd);
    }

    [McpServerTool]
    [Description("BRAIN/placement: where new code of a kind belongs AND how it's written here, inheriting project/layer conventions up the broader chain (nearest wins) plus belongs_in and forbidden antipatterns. One recursive query. Call BEFORE writing new code so it's right-place/right-style first try. Args: codekind (e.g. vue-component, dotnet-api-endpoint, kotlin-compose-screen).")]
    public static string brain_place(string codekind)
    {
        using SqliteConnection con = Open();
        string kind = codekind.Contains(':') ? codekind.Trim() : "kind:" + codekind.Trim();
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = @"WITH RECURSIVE chain(k,depth) AS (
              SELECT $kind, 0 UNION ALL
              SELECT t.o, c.depth+1 FROM triple_now t JOIN chain c ON t.s = c.k WHERE t.p = 'broader' AND c.depth < 6),
            ranked AS (SELECT s.name, s.value, s.facet, s.because,
                   ROW_NUMBER() OVER (PARTITION BY s.name ORDER BY c.depth, s.id) AS rn
                   FROM chain c JOIN slot_now s ON s.frame_k = c.k)
            SELECT name AS slot, value, because FROM ranked WHERE rn = 1
            UNION ALL SELECT 'belongs_in', t.o, n.gloss FROM triple_now t JOIN node_now n ON n.k=t.o WHERE t.s=$kind AND t.p='belongs_in'
            UNION ALL SELECT 'forbidden', t.o, n.gloss FROM triple_now t JOIN node_now n ON n.k=t.o WHERE t.s=$kind AND t.p='forbids'
            ORDER BY 1";
        cmd.Parameters.AddWithValue("$kind", kind);
        string placed = Rows(cmd);
        Reinforce(con, new[] { kind });
        return placed == "(nothing)" ? placed + LogGap(con, "brain_place", codekind) : placed;
    }

    [McpServerTool]
    [Description("BRAIN/recall: a natural-language question -> DB-side synonym expansion -> FTS seed -> same-statement traversal to who-uses-it + the governing rule + the ground-truth fact. The catch-all when the question isn't clearly scope/common/place. One query. Args: query.")]
    public static string brain_recall(string query)
    {
        using SqliteConnection con = Open();
        List<string> toks = Tokens(query);
        if (toks.Count == 0) return "no usable query terms.";
        string rawList = string.Join(",", toks.Select((_, i) => $"($q{i})"));
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = $@"WITH q(raw) AS (VALUES {rawList}),
            expanded AS (SELECT raw AS term FROM q UNION SELECT a.canonical FROM q JOIN term_alias a ON a.term = q.raw),
            match_expr AS (SELECT group_concat('""'||term||'""',' OR ') AS m FROM expanded),
            seed AS (SELECT n.id, n.k,
                       bm25(node_fts) - 0.15*MIN(COALESCE(u.hits,0),20)
                       - CASE WHEN u.last_used IS NULL THEN 0
                              WHEN julianday('now')-julianday(u.last_used) < 1 THEN 0.6
                              WHEN julianday('now')-julianday(u.last_used) < 7 THEN 0.3
                              WHEN julianday('now')-julianday(u.last_used) < 30 THEN 0.1
                              ELSE 0 END AS r
                     FROM node_fts f JOIN node_now n ON n.id = f.rowid
                     LEFT JOIN usage u ON u.node_k = n.k
                     WHERE node_fts MATCH (SELECT m FROM match_expr) ORDER BY r LIMIT 8)
            SELECT n.k, n.label, n.gloss,
                   substr((SELECT group_concat(DISTINCT t.s) FROM triple_now t WHERE t.o = n.k AND t.p IN ('consumes','governed_by','exposes','implements')),1,100) AS used_by,
                   substr((SELECT m.body FROM ref r JOIN memory m ON m.k = r.payload_k WHERE r.node_k = n.k AND r.channel='memory' LIMIT 1),1,220) AS rule,
                   substr((SELECT fa.value FROM ref r JOIN facts fa ON fa.k = r.payload_k WHERE r.node_k = n.k AND r.channel='facts' LIMIT 1),1,220) AS fact
            FROM seed JOIN node_now n ON n.id = seed.id ORDER BY seed.r";
        for (int i = 0; i < toks.Count; i++) cmd.Parameters.AddWithValue($"$q{i}", toks[i]);
        string res = RowsK(con, cmd);
        if (res != "(nothing)") return res;
        // FTS + synonyms missed — substring fallback over label/gloss
        using SqliteCommand fb = con.CreateCommand();
        string likeClauses = string.Join(" OR ", toks.Select((_, i) => $"label LIKE $l{i} OR gloss LIKE $l{i}"));
        fb.CommandText = $"SELECT k, label, gloss FROM node_now WHERE {likeClauses} LIMIT 8";
        for (int i = 0; i < toks.Count; i++) fb.Parameters.AddWithValue($"$l{i}", "%" + toks[i] + "%");
        string fbRes = RowsK(con, fb);
        return fbRes == "(nothing)" ? "(nothing)" + LogGap(con, "brain_recall", query) : "(substring fallback)\n" + fbRes;
    }

    [McpServerTool]
    [Description("BRAIN/impact: every cross-project consumer of a shared symbol/contract (project/file/line/hardcoded) from the live edges graph plus the summary triple. Call BEFORE changing any shared contract. Args: symbol_or_contract (e.g. device_id, envelope, has_more).")]
    public static string brain_impact(string symbol)
    {
        using SqliteConnection con = Open();
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT s AS project, o AS contract_symbol, file, line, hardcoded FROM legacy_consumes WHERE o LIKE $like
            UNION ALL SELECT substr(t.s,6), t.o, NULL, NULL, NULL FROM triple_now t WHERE t.p='consumes' AND t.o LIKE $like AND t.o_is_literal=0
            LIMIT 40";
        cmd.Parameters.AddWithValue("$like", "%" + symbol + "%");
        return RowsK(con, cmd);
    }

    [McpServerTool]
    [Description("BRAIN/write-last: persist a durable node, triple, or slot learned this turn (the continuous-update half of the protocol). Use AGGRESSIVELY: any new durable fact, correction, convention, contract, URL, or path you encounter is worth a write — writes are cheap, re-deriving is not, and a matching open gap auto-resolves. kind='node' -> key, a=nodekind, b=label, c=gloss; kind='triple' -> key=subject, a=predicate, b=object; kind='slot' -> key=frame, a=name, b=value. o_is_literal is derived; triggers reject typo'd predicates and dangling refs. Put anecdote/why in `because`, never the gloss.")]
    public static string brain_learn(string kind, string key, string a = "", string b = "", string c = "", string because = "", bool hard = false)
    {
        using SqliteConnection con = Open();
        string result = LearnCore(con, kind, key, a, b, c, because, hard);
        if (!result.StartsWith("rejected", StringComparison.Ordinal) && !result.StartsWith("unknown", StringComparison.Ordinal) && !result.StartsWith("kind must", StringComparison.Ordinal))
            ResolveGaps(con, $"{key} {a} {b} {c} {because}");
        return result;
    }

    private static string LearnCore(SqliteConnection con, string kind, string key, string a, string b, string c, string because, bool hard)
    {
        try
        {
            if (kind == "node")
            {
                string guardErr = NodeGuard(a, b.Length > 0 ? b : key);
                if (guardErr.Length > 0) return guardErr;
                using SqliteCommand exists = con.CreateCommand();
                exists.CommandText = "SELECT kind||'|'||label||'|'||gloss||'|'||hard FROM node_now WHERE k=$k";
                exists.Parameters.AddWithValue("$k", key);
                string sig = $"{a}|{b}|{c}|{(hard ? 1 : 0)}";
                if (Convert.ToString(exists.ExecuteScalar()) == sig) return "noop (unchanged).";
                using SqliteCommand close = con.CreateCommand();
                close.CommandText = "UPDATE node SET valid_to=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE k=$k AND valid_to IS NULL";
                close.Parameters.AddWithValue("$k", key); close.ExecuteNonQuery();
                using SqliteCommand ins = con.CreateCommand();
                ins.CommandText = "INSERT INTO node(k,kind,label,gloss,hard) VALUES($k,$ki,$l,$g,$h)";
                ins.Parameters.AddWithValue("$k", key); ins.Parameters.AddWithValue("$ki", a);
                ins.Parameters.AddWithValue("$l", b.Length > 0 ? b : key); ins.Parameters.AddWithValue("$g", c);
                ins.Parameters.AddWithValue("$h", hard ? 1 : 0); ins.ExecuteNonQuery();
                return "node learned.";
            }
            if (kind == "triple")
            {
                using SqliteCommand lit = con.CreateCommand();
                lit.CommandText = "SELECT 1-is_link FROM pred_vocab WHERE p=$p";
                lit.Parameters.AddWithValue("$p", a);
                object? l = lit.ExecuteScalar();
                if (l is null) return $"unknown predicate '{a}'.";
                using SqliteCommand exists = con.CreateCommand();
                exists.CommandText = "SELECT count(*) FROM triple_now WHERE s=$s AND p=$p AND o=$o";
                exists.Parameters.AddWithValue("$s", key); exists.Parameters.AddWithValue("$p", a); exists.Parameters.AddWithValue("$o", b);
                if ((long)(exists.ExecuteScalar() ?? 0L) > 0)
                {
                    using SqliteCommand bump = con.CreateCommand();
                    bump.CommandText = "UPDATE triple SET conf=MIN(conf+0.2,3.0) WHERE s=$s AND p=$p AND o=$o AND valid_to IS NULL";
                    bump.Parameters.AddWithValue("$s", key); bump.Parameters.AddWithValue("$p", a); bump.Parameters.AddWithValue("$o", b);
                    bump.ExecuteNonQuery();
                    return "reinforced (known relation, confidence bumped).";
                }
                using SqliteCommand ins = con.CreateCommand();
                ins.CommandText = "INSERT INTO triple(s,p,o,o_is_literal,because,src,hard) VALUES($s,$p,$o,$lit,$b,'mcp',$h)";
                ins.Parameters.AddWithValue("$s", key); ins.Parameters.AddWithValue("$p", a); ins.Parameters.AddWithValue("$o", b);
                ins.Parameters.AddWithValue("$lit", l); ins.Parameters.AddWithValue("$b", because); ins.Parameters.AddWithValue("$h", hard ? 1 : 0);
                ins.ExecuteNonQuery();
                return "triple learned.";
            }
            if (kind == "slot")
            {
                using SqliteCommand close = con.CreateCommand();
                close.CommandText = "UPDATE slot SET valid_to=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE frame_k=$f AND name=$n AND multi=0 AND valid_to IS NULL";
                close.Parameters.AddWithValue("$f", key); close.Parameters.AddWithValue("$n", a); close.ExecuteNonQuery();
                using SqliteCommand ins = con.CreateCommand();
                ins.CommandText = "INSERT INTO slot(frame_k,name,value,because,src) VALUES($f,$n,$v,$b,'mcp')";
                ins.Parameters.AddWithValue("$f", key); ins.Parameters.AddWithValue("$n", a); ins.Parameters.AddWithValue("$v", b);
                ins.Parameters.AddWithValue("$b", because); ins.ExecuteNonQuery();
                return "slot learned.";
            }
            return "kind must be node | triple | slot.";
        }
        catch (SqliteException e)
        {
            return "rejected: " + e.Message;
        }
    }

    [McpServerTool]
    [Description("BRAIN/gaps: what the brain could NOT answer — every refused fact/rule/recall/doc/place query is auto-logged here. Check at session start or before research: an open gap you can now answer should be staged via brain_stage (a matching learn auto-resolves it). No args.")]
    public static string brain_gaps()
    {
        using SqliteConnection con = Open();
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = "SELECT misses||'x', tool, query, substr(last_ts,1,10) FROM gaps WHERE status='open' ORDER BY misses DESC, last_ts DESC LIMIT 15";
        string rows = Rows(cmd);
        if (rows == "(nothing)") return "no open gaps — every recent lookup was answerable.";
        long open = Count(con, "SELECT count(*) FROM gaps WHERE status='open'");
        return (open > 15 ? $"{open} open gaps, top 15:\n" : "") + rows;
    }

    [McpServerTool]
    [Description("Show the change history (the cold append-only mutation log) for an entity, for tracing when something went wrong. Latest 30.")]
    public static string history(string term)
    {
        using SqliteConnection con = Open();
        return new HistoryTool().ExecuteMcp(con, term);
    }

    [McpServerTool]
    [Description("Log a FINDING: an unrelated bug or issue spotted during other work, to surface to the operator later (not now).")]
    public static string log_finding(string title, string detail = "", string source = "")
    {
        using SqliteConnection con = Open();
        return new FindingTool().ExecuteMcp(con, title, detail, source);
    }

    [McpServerTool]
    [Description("List open findings (unrelated issues spotted earlier) to surface to the operator.")]
    public static string open_findings()
    {
        using SqliteConnection con = Open();
        return new FindingsTool().ExecuteMcp(con);
    }

    private static string LedgerPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm", ResolveInstance(), "pending-learn.jsonl");

    private static string JStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool JBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    [McpServerTool]
    [Description("BRAIN/write-side brake: STAGE a durable learning the instant you notice it (a cheap append, no DB write) — same args as brain_learn. The Stop hook refuses to end the turn while anything staged is unflushed, so nothing you stage is ever silently dropped (the rot that killed MEMORY.md). Stage AGGRESSIVELY — the MOMENT the operator corrects you, a lookup comes back wrong/empty, or a new ground-truth fact/convention/contract appears; when in doubt, stage it. Don't defer to turn-end and forget. Commit the batch with brain_flush.")]
    public static string brain_stage(string kind, string key, string a = "", string b = "", string c = "", string because = "", bool hard = false)
    {
        string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");

        // "kind" names the ROW TYPE, and for a node row the node's OWN kind goes in "a" — two things
        // called kind, one parameter named for both. Every caller reads it the obvious way and passes
        // "rule", and the tool rejected it every single time. A perfect error message that fires on
        // every call is an API defect, not a user defect.
        //
        // The intent was never ambiguous: row types and node kinds are disjoint sets, so "rule" can
        // only ever have meant a node of kind rule. Accept it. The remaining arguments simply shift
        // left by the slot the caller did not know to leave empty: a is the label, b the statement.
        bool coerced = !RowKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)
            && NodeKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);
        if (coerced) (kind, a, b, c) = ("node", kind, a, b.Length > 0 ? b : c);

        if (kind == "node")
        {
            string guardErr = NodeGuard(a, b.Length > 0 ? b : key);
            if (guardErr.Length > 0) return guardErr;
        }
        string h = hard ? "true" : "false";
        string line = kind switch
        {
            "node" => "{" + $"\"k\":\"node\",\"key\":\"{Esc(key)}\",\"kind\":\"{Esc(a)}\",\"label\":\"{Esc(b)}\",\"gloss\":\"{Esc(c)}\",\"scheme\":\"\",\"hard\":{h}" + "}",
            "triple" => "{" + $"\"k\":\"triple\",\"s\":\"{Esc(key)}\",\"p\":\"{Esc(a)}\",\"o\":\"{Esc(b)}\",\"because\":\"{Esc(because)}\",\"hard\":{h}" + "}",
            "slot" => "{" + $"\"k\":\"slot\",\"frame\":\"{Esc(key)}\",\"name\":\"{Esc(a)}\",\"value\":\"{Esc(b)}\",\"facet\":\"text\",\"multi\":false,\"because\":\"{Esc(because)}\"" + "}",
            _ => "",
        };
        if (line.Length == 0) return WrongRowKind(kind);
        // A brand-new instance has no directory yet, and AppendAllText does not make one — staging into
        // a fresh instance threw, and the MCP layer reported only "An error occurred invoking
        // brain_stage", so the learning was lost with no way to see why.
        string ledger = LedgerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
        File.AppendAllText(ledger, line + "\n");
        return coerced
            ? $"staged node {key} as kind \"{a}\" (owe brain_flush). Note: the first argument is the ROW type — node / triple / slot — and \"{a}\" is the node's own kind, so it was moved for you."
            : $"staged {kind} {key} (owe brain_flush).";
    }

    [McpServerTool]
    [Description("BRAIN/write-side brake: COMMIT every staged learning into the brain in one pass, then clear the ledger so the Stop hook releases. Re-uses brain_learn for each, so supersession / conf-reinforcement / predicate + dangling-ref guards apply identically. Call at turn end (or any time) to persist what you staged.")]
    public static string brain_flush()
    {
        string ledger = LedgerPath();
        if (!File.Exists(ledger)) return "nothing staged.";
        string[] lines = File.ReadAllLines(ledger).Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0) { File.Delete(ledger); return "nothing staged."; }
        int n = 0;
        StringBuilder rejects = new();
        // Lines that failed on a TRANSIENT lock are kept in the ledger so the next flush retries them —
        // a locked DB under concurrent MCP writers must never silently drop a staged learning (it did,
        // once: two learnings lost when the whole ledger was deleted on a "database is locked" reject).
        // Permanent rejects (supersession no-op, unknown kind, dangling ref) are reported and dropped;
        // retrying them would loop forever.
        List<string> keepForRetry = new();
        foreach (string line in lines)
        {
            using JsonDocument d = JsonDocument.Parse(line);
            JsonElement e = d.RootElement;
            string res = JStr(e, "k") switch
            {
                "node" => brain_learn("node", JStr(e, "key"), JStr(e, "kind"), JStr(e, "label"), JStr(e, "gloss"), "", JBool(e, "hard")),
                "triple" => brain_learn("triple", JStr(e, "s"), JStr(e, "p"), JStr(e, "o"), "", JStr(e, "because"), JBool(e, "hard")),
                "slot" => brain_learn("slot", JStr(e, "frame"), JStr(e, "name"), JStr(e, "value"), "", JStr(e, "because"), false),
                _ => "skip (unknown kind).",
            };
            bool rejected = res.StartsWith("rejected", StringComparison.Ordinal) || res.StartsWith("unknown", StringComparison.Ordinal) || res.StartsWith("skip", StringComparison.Ordinal);
            if (rejected)
            {
                rejects.AppendLine($"  ! {line} -> {res}");
                if (res.Contains("locked", StringComparison.OrdinalIgnoreCase) || res.Contains("busy", StringComparison.OrdinalIgnoreCase))
                    keepForRetry.Add(line);
            }
            else
                n++;
        }
        if (keepForRetry.Count > 0) File.WriteAllLines(ledger, keepForRetry);
        else File.Delete(ledger);
        string retryNote = keepForRetry.Count > 0 ? $" {keepForRetry.Count} kept for retry (DB was locked — flush again)." : "";
        return $"flushed {n} learning(s) into the brain.{retryNote}" + (rejects.Length > 0 ? "\n" + rejects : "");
    }

    // --- Code graph: graph_query / graph_path / graph_explain -------------------------------------
    // All three read the SAME `edges` table `impact` reads: each row is a (symbol, file, line, project)
    // fact, either a declaration (contract='decl') or a curated usage site. There is no separate
    // call-graph table, so "defined-in" and "uses" are both read off this one table.

    private const int GraphLineCap = 40;

    // A LIKE match on the raw symbol column hits substrings anywhere ("stand" inside "TrackerStands
    // Down"). Requiring a token to equal the whole symbol or one of its case/underscore-separated parts
    // is what keeps a natural-language question from pulling in unrelated symbols sharing a query word.
    private static bool SymbolMatchesTokens(string symbol, List<string> toks)
    {
        string name = symbol.ToLowerInvariant();
        HashSet<string> parts = new(StringComparer.OrdinalIgnoreCase) { name };
        foreach (string part in Regex.Split(symbol, "_|(?<=[a-z0-9])(?=[A-Z])"))
            if (part.Length > 0) parts.Add(part.ToLowerInvariant());
        return toks.Any(t => parts.Contains(t));
    }

    // Hard ceiling on total output lines — an agent calling this pays context for every line back.
    private static string CapLines(List<string> lines, int max = GraphLineCap)
    {
        if (lines.Count <= max) return string.Join("\n", lines);
        return string.Join("\n", lines.Take(max)) + $"\n(+{lines.Count - max} more line(s) truncated — narrow the query)";
    }

    // Resolve a loose name to a live graph node: an exact symbol (any case) first, then a file whose
    // path ends with the given text. Symbols and files share no namespace, so the first hit is unambiguous.
    private static (string kind, string value)? ResolveGraphNode(SqliteConnection con, string input)
    {
        string norm = input.Trim();
        if (norm.Length == 0) return null;
        using (SqliteCommand c = con.CreateCommand())
        {
            c.CommandText = "SELECT symbol FROM edges WHERE symbol=$s COLLATE NOCASE LIMIT 1";
            c.Parameters.AddWithValue("$s", norm);
            if (c.ExecuteScalar() is string sym) return ("symbol", sym);
        }
        using (SqliteCommand c = con.CreateCommand())
        {
            c.CommandText = "SELECT file FROM edges WHERE file LIKE $f LIMIT 1";
            c.Parameters.AddWithValue("$f", "%" + norm.Replace('\\', '/'));
            if (c.ExecuteScalar() is string file) return ("file", file);
        }
        return null;
    }

    // Breadth-first search over the symbol<->file bipartite graph: a symbol's hop is its declaration/
    // usage files, a file's hop is the symbols it carries. Depth caps at 6 (past that a "connection" is
    // coincidence, not a fact worth reporting) and a hard expansion budget stops one generic symbol
    // (or hub file) with thousands of neighbors from turning a sub-second lookup into a table scan.
    // IMPORTANT: no COLLATE NOCASE on the hot per-hop queries below — every kind/value here came out of
    // this same table already in its exact stored casing, and NOCASE would silently defeat
    // edges_ident_idx / edges_file_idx on every single hop (measured: ~9s instead of ~0.3s on the real store).
    private static List<(string toKind, string toVal, string file, int line, string rel)>? GraphBfs(
        SqliteConnection con, (string kind, string value) from, (string kind, string value) to, int maxDepth = 6)
    {
        using (SqliteCommand idx = con.CreateCommand())
        {
            idx.CommandText = "CREATE INDEX IF NOT EXISTS edges_file_idx ON edges(file, symbol, line)";
            idx.ExecuteNonQuery();
        }
        if (from.kind == to.kind && string.Equals(from.value, to.value, StringComparison.OrdinalIgnoreCase)) return new();

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { $"{from.kind}:{from.value}" };
        List<(string kind, string value, List<(string toKind, string toVal, string file, int line, string rel)> hops)> frontier = new()
        {
            (from.kind, from.value, new List<(string, string, string, int, string)>())
        };

        int expansions = 2000;
        for (int depth = 0; depth < maxDepth; depth++)
        {
            List<(string kind, string value, List<(string toKind, string toVal, string file, int line, string rel)> hops)> next = new();
            foreach ((string kind, string value, List<(string toKind, string toVal, string file, int line, string rel)> hops) cur in frontier)
            {
                if (expansions-- <= 0) return null;
                using SqliteCommand c = con.CreateCommand();
                string rel = cur.kind == "symbol" ? "defined-in" : "uses";
                c.CommandText = cur.kind == "symbol"
                    ? "SELECT DISTINCT file, line FROM edges WHERE symbol=$k LIMIT 60"
                    : "SELECT DISTINCT symbol, line FROM edges WHERE file=$k LIMIT 60";
                c.Parameters.AddWithValue("$k", cur.value);
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read())
                {
                    string neighborVal = r.GetString(0);
                    int line = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                    string neighborKind = cur.kind == "symbol" ? "file" : "symbol";
                    string key = $"{neighborKind}:{neighborVal}";
                    if (!seen.Add(key)) continue;
                    string hopFile = cur.kind == "symbol" ? neighborVal : cur.value;
                    List<(string toKind, string toVal, string file, int line, string rel)> hops = new(cur.hops)
                    {
                        (neighborKind, neighborVal, hopFile, line, rel)
                    };
                    if (neighborKind == to.kind && string.Equals(neighborVal, to.value, StringComparison.OrdinalIgnoreCase)) return hops;
                    next.Add((neighborKind, neighborVal, hops));
                }
            }
            if (next.Count == 0) break;
            frontier = next;
        }
        return null;
    }

    [McpServerTool]
    [Description("Find the most relevant symbols, files and docs for a natural-language question, grouped by project, with one hop of neighbors (declaration file, top consuming projects). No LLM — word-boundary symbol matching plus the existing docs_fts lookup. Use to orient in an unfamiliar area of the codebase before making a change.")]
    public static string graph_query(string question)
    {
        using SqliteConnection con = Open();
        List<string> toks = Tokens(question);
        if (toks.Count == 0) return "no usable query terms.";

        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string t in toks)
        {
            using SqliteCommand c = con.CreateCommand();
            c.CommandText = "SELECT DISTINCT symbol FROM edges WHERE symbol LIKE $p LIMIT 400";
            c.Parameters.AddWithValue("$p", "%" + t + "%");
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) candidates.Add(r.GetString(0));
        }
        List<string> matched = candidates.Where(s => SymbolMatchesTokens(s, toks)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(20).ToList();

        List<string> lines = new();
        if (matched.Count == 0)
        {
            lines.Add($"no symbol matches \"{question}\".{LogGap(con, "graph_query", question)}");
        }
        else
        {
            List<(string symbol, string project, string defLoc, List<(string project, int files)> byProject)> rows = new();
            foreach (string sym in matched)
            {
                string? defLoc = null;
                string homeProject = "";
                using (SqliteCommand c = con.CreateCommand())
                {
                    c.CommandText = "SELECT project, file, line FROM edges WHERE symbol=$s AND contract='decl' LIMIT 1";
                    c.Parameters.AddWithValue("$s", sym);
                    using SqliteDataReader r = c.ExecuteReader();
                    if (r.Read()) { homeProject = r.GetString(0); defLoc = $"{r.GetString(1)}:{r.GetInt32(2)}"; }
                }
                List<(string project, int files)> byProject = new();
                using (SqliteCommand c = con.CreateCommand())
                {
                    c.CommandText = @"SELECT project, COUNT(DISTINCT file) FROM edges
                        WHERE symbol=$s AND project IS NOT NULL AND project != ''
                        GROUP BY project ORDER BY 2 DESC LIMIT 5";
                    c.Parameters.AddWithValue("$s", sym);
                    using SqliteDataReader r = c.ExecuteReader();
                    while (r.Read()) byProject.Add((r.GetString(0), r.GetInt32(1)));
                }
                if (homeProject.Length == 0) homeProject = byProject.FirstOrDefault().project ?? "(unknown)";
                rows.Add((sym, homeProject, defLoc ?? "(no indexed declaration)", byProject));
            }

            lines.Add($"{matched.Count} symbol(s) matched across {rows.Select(r => r.project).Distinct().Count()} project(s):");
            foreach (IGrouping<string, (string symbol, string project, string defLoc, List<(string project, int files)> byProject)> g in rows.GroupBy(r => r.project).OrderByDescending(g => g.Count()))
            {
                lines.Add($"[{g.Key}]");
                foreach ((string symbol, string project, string defLoc, List<(string project, int files)> byProject) row in g)
                {
                    lines.Add($"  {row.symbol}  defined: {row.defLoc}");
                    if (row.byProject.Count > 0)
                        lines.Add("    used by: " + string.Join(", ", row.byProject.Select(p => $"{p.project} ({p.files} file(s))")));
                }
            }
        }

        string match = Match(question);
        if (match.Length > 0)
        {
            try
            {
                using SqliteCommand c = con.CreateCommand();
                c.CommandText = @"SELECT d.title, d.path FROM (SELECT k, bm25(docs_fts) AS s FROM docs_fts WHERE docs_fts MATCH $m ORDER BY s LIMIT 3) x
                    JOIN docs d ON d.k=x.k ORDER BY x.s";
                c.Parameters.AddWithValue("$m", match);
                using SqliteDataReader r = c.ExecuteReader();
                bool any = false;
                while (r.Read())
                {
                    if (!any) { lines.Add("docs:"); any = true; }
                    lines.Add($"  • {Clip(r.GetString(0), 70)} ({Clip(r.GetString(1), 60)})");
                }
            }
            catch (SqliteException) { /* docs not indexed for this instance */ }
        }
        return CapLines(lines);
    }

    [McpServerTool]
    [Description("Shortest path between two symbols or files over the code graph (declaration + usage edges), max depth 6, each hop printed with file:line. Use to answer \"how does A connect to B\" without tracing declaration-then-usage chains by hand.")]
    public static string graph_path(string a, string b)
    {
        using SqliteConnection con = Open();
        (string kind, string value)? from = ResolveGraphNode(con, a);
        (string kind, string value)? to = ResolveGraphNode(con, b);
        if (from == null) return $"no symbol or file matches \"{a}\".";
        if (to == null) return $"no symbol or file matches \"{b}\".";

        List<(string toKind, string toVal, string file, int line, string rel)>? hops = GraphBfs(con, from.Value, to.Value);
        if (hops == null) return $"no path found between \"{a}\" and \"{b}\" within depth 6.";
        if (hops.Count == 0) return "same node.";

        List<string> lines = new()
        {
            $"[{from.Value.kind}] {from.Value.value}  ->  [{to.Value.kind}] {to.Value.value}   ({hops.Count} hop(s))"
        };
        foreach ((string toKind, string toVal, string file, int line, string rel) h in hops)
        {
            string loc = h.toKind == "file" ? $"line {h.line}" : $"{h.file}:{h.line}";
            lines.Add($"  {h.rel}  [{h.toKind}] {h.toVal}   {loc}");
        }
        return CapLines(lines);
    }

    [McpServerTool]
    [Description("What a symbol IS (kind, definition file:line), who uses it (grouped by project, top 10 sites), and docs/rules that mention it. No call-graph data exists yet, so \"what it uses\" is reported as not tracked rather than guessed. Use before renaming or reasoning about a symbol you did not just write.")]
    public static string graph_explain(string symbol)
    {
        using SqliteConnection con = Open();
        if (symbol.Trim().Length == 0) return "provide a symbol name.";
        string? resolved;
        using (SqliteCommand c = con.CreateCommand())
        {
            c.CommandText = "SELECT symbol FROM edges WHERE symbol=$s COLLATE NOCASE LIMIT 1";
            c.Parameters.AddWithValue("$s", symbol);
            resolved = c.ExecuteScalar() as string;
        }
        if (resolved == null)
        {
            using SqliteCommand c = con.CreateCommand();
            c.CommandText = "SELECT symbol FROM edges WHERE symbol LIKE $s ORDER BY length(symbol) LIMIT 1";
            c.Parameters.AddWithValue("$s", "%" + symbol + "%");
            resolved = c.ExecuteScalar() as string;
        }
        if (resolved == null) return $"no symbol matches \"{symbol}\".{LogGap(con, "graph_explain", symbol)}";

        List<string> lines = new();
        List<(string project, string file, int line, string usage)> declRows = new();
        using (SqliteCommand c = con.CreateCommand())
        {
            c.CommandText = "SELECT project, file, line, usage FROM edges WHERE symbol=$s AND contract='decl' ORDER BY project, file LIMIT 5";
            c.Parameters.AddWithValue("$s", resolved);
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) declRows.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
        }
        string kind = declRows.Count > 0 ? declRows[0].usage : "unknown";
        if (declRows.Count == 0)
        {
            using SqliteCommand c = con.CreateCommand();
            c.CommandText = "SELECT contract FROM edges WHERE symbol=$s AND contract != '' LIMIT 1";
            c.Parameters.AddWithValue("$s", resolved);
            if (c.ExecuteScalar() is string contractKind) kind = contractKind;
        }
        lines.Add($"{resolved}  [{kind}]");
        if (declRows.Count == 0) lines.Add("  defined: (no indexed declaration — curated usage edge only, or not yet indexed)");
        foreach ((string project, string file, int line, string usage) d in declRows)
            lines.Add($"  defined: {d.project}  {d.file}:{d.line}");

        // Known-issue fix (RESTRUCTURE.md slice 13): contract != 'decl' excludes a symbol's own
        // declaration row from its "used by" count — a declaration is not a use, and is already
        // reported above as "defined:".
        List<(string project, int sites, int files)> byProject = new();
        using (SqliteCommand c = con.CreateCommand())
        {
            c.CommandText = @"SELECT project, COUNT(*), COUNT(DISTINCT file) FROM edges
                WHERE symbol=$s AND project IS NOT NULL AND project != '' AND contract != 'decl'
                GROUP BY project ORDER BY 2 DESC";
            c.Parameters.AddWithValue("$s", resolved);
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) byProject.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        }
        lines.Add($"used by ({byProject.Sum(p => p.sites)} site(s) across {byProject.Count} project(s)):");
        foreach ((string project, int sites, int files) p in byProject)
            lines.Add($"  {p.project,-14} {p.sites} site(s), {p.files} file(s)");

        lines.Add("top sites:");
        using (SqliteCommand c = con.CreateCommand())
        {
            c.CommandText = @"SELECT project, file, line, usage, hardcoded FROM edges WHERE symbol=$s
                ORDER BY hardcoded DESC, project, file LIMIT 10";
            c.Parameters.AddWithValue("$s", resolved);
            using SqliteDataReader r = c.ExecuteReader();
            int n = 0;
            while (r.Read())
            {
                n++;
                bool hard = r.GetInt32(4) == 1;
                lines.Add($"  {r.GetString(0),-10} {r.GetString(1)}:{r.GetInt32(2)}{(hard ? " [HARDCODED]" : "")}  {Clip(r.GetString(3), 60)}");
            }
            if (n == 0) lines.Add("  (none recorded)");
        }

        // The edges table records where a symbol is DECLARED and USED, not what it itself calls — there
        // is no call-graph edge to read, so this is honest about the gap instead of guessing from co-location.
        lines.Add("uses: not tracked — the edges table records declaration/usage sites, not call relationships.");

        string match = Match(resolved);
        if (match.Length > 0)
        {
            try
            {
                using SqliteCommand c = con.CreateCommand();
                c.CommandText = @"SELECT title, path FROM (SELECT k, bm25(docs_fts) AS s FROM docs_fts WHERE docs_fts MATCH $m ORDER BY s LIMIT 2) x
                    JOIN docs d ON d.k=x.k ORDER BY x.s";
                c.Parameters.AddWithValue("$m", match);
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read()) lines.Add($"  doc: {Clip(r.GetString(0), 60)} ({Clip(r.GetString(1), 50)})");
            }
            catch (SqliteException) { /* docs not indexed */ }
            try
            {
                using SqliteCommand c = con.CreateCommand();
                c.CommandText = @"SELECT hook FROM (SELECT k, bm25(memory_fts) AS s FROM memory_fts WHERE memory_fts MATCH $m ORDER BY s LIMIT 2) x
                    JOIN memory mem ON mem.k=x.k ORDER BY x.s";
                c.Parameters.AddWithValue("$m", match);
                using SqliteDataReader r = c.ExecuteReader();
                while (r.Read()) lines.Add($"  rule: {Clip(r.GetString(0), 70)}");
            }
            catch (SqliteException) { /* memory not indexed */ }
        }
        return CapLines(lines);
    }
}
