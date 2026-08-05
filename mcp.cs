#:package ModelContextProtocol@1.3.0
#:package Microsoft.Extensions.Hosting@9.0.0
#:package Microsoft.Data.Sqlite@10.0.9
// Pinned to the safe floor: Microsoft.Data.Sqlite otherwise resolves SQLitePCLRaw 2.1.10, which
// carries GHSA-2m69-gcr7-jv3q. Keep in lockstep with aitm.cs.
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3
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

    // bm25 lower = better; matches weaker than this are low-IDF noise, refused once the corpus is large
    // enough for IDF to discriminate. Mirrors the CLI's calibrated floor.
    private const double RelevanceFloor = -3.0;
    private const int FloorMinCorpus = 20;

    // Output discipline: every tool answer is context the caller pays for. Cells clip, payloads clip,
    // and the whole answer stays under OutCap (brain_core gets CoreCap — it is the once-per-session
    // always-on set and must not lose hard rules to trimming).
    private const int CellCap = 160;
    private const int BodyCap = 240;
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
            busy.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
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

    // Channel hits feed the same usage signal as graph reads: surfacing a fact/rule reinforces the
    // brain nodes ref-bridged to it, so channel use also makes recall ranking sharper.
    private static void ReinforceChannel(SqliteConnection con, string channel, IEnumerable<string> payloadKeys)
    {
        foreach (string payloadKey in payloadKeys.Where(s => s.Length > 0).Distinct())
        {
            using SqliteCommand u = con.CreateCommand();
            u.CommandText = @"INSERT INTO usage(node_k,hits,last_used)
                SELECT node_k,1,strftime('%Y-%m-%dT%H:%M:%fZ','now') FROM ref WHERE channel=$c AND payload_k=$p
                ON CONFLICT(node_k) DO UPDATE SET hits=hits+1, last_used=strftime('%Y-%m-%dT%H:%M:%fZ','now')";
            u.Parameters.AddWithValue("$c", channel);
            u.Parameters.AddWithValue("$p", payloadKey);
            try { u.ExecuteNonQuery(); } catch (SqliteException) { }
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
        List<string> qToks = Tokens(query);
        if (qToks.Count == 0) return "no usable query terms.";
        string match = string.Join(" OR ", qToks.Select(t => $"\"{t}\""));
        bool gate = Count(con, "SELECT count(*) FROM facts") >= FloorMinCorpus;
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT f.term,f.category,f.value,f.source,x.s,f.aliases,f.notes,f.k
            FROM (SELECT k, bm25(facts_fts) AS s FROM facts_fts WHERE facts_fts MATCH $m ORDER BY s LIMIT 3) x
            JOIN facts f ON f.k=x.k ORDER BY x.s";
        cmd.Parameters.AddWithValue("$m", match);
        StringBuilder sb = new();
        List<string> hitKeys = new();
        using (SqliteDataReader r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (gate && r.GetDouble(4) > RelevanceFloor) continue; // weak/low-IDF match — refuse rather than surface noise
                string hay = $"{r.GetString(0)} {r.GetString(5)} {r.GetString(1)} {r.GetString(2)} {r.GetString(6)}".ToLowerInvariant();
                int present = qToks.Count(t => hay.Contains(t, StringComparison.Ordinal));
                if (qToks.Count >= 3 && present <= 1) continue; // 3+ token question matching one tangential word — not an answer
                hitKeys.Add(r.GetString(7));
                sb.AppendLine($"• {r.GetString(0)} [{r.GetString(1)}]\n  {r.GetString(2)}\n  source: {Clip(r.GetString(3), CellCap)}");
            }
        }
        if (sb.Length == 0)
            return $"no confident answer for \"{query}\" — not in the knowledge base (refusing rather than guessing).{LogGap(con, "fact", query)}";
        ReinforceChannel(con, "facts", hitKeys);
        return Budget(sb.ToString());
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
    [Description("INTERNAL TESTING ONLY. Mint a real IdP user access token for a subject (user GUID, email, or username) via the supported token-exchange grant, with audience=nomercy-server so the media-server accepts it. Defaults to the dev realm; pass realm=\"prod\" for auth.nomercy.tv. GATED: does nothing unless AITM_ALLOW_TOKEN_MINT=1 is set in the environment, because an always-on impersonation primitive is a large blast radius. Never fabricates a user — a subject with no matching account is refused. Requires IDP_ADMIN_CLIENT_SECRET (the nomercy-api service account).")]
    public static string idp_token(string subject, string realm = "dev")
    {
        if (Environment.GetEnvironmentVariable("AITM_ALLOW_TOKEN_MINT") != "1")
        {
            return "refused: token minting is gated. This tool impersonates a real user, so it is off by "
                + "default. Set AITM_ALLOW_TOKEN_MINT=1 in the environment to enable it for this session, "
                + "or run the script directly: node idp-impersonate.mjs <subject> [--prod].";
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
        string match = Match(query);
        if (match.Length == 0) return "no usable query terms.";
        try
        {
            using SqliteCommand cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT ch.ts,ch.text
                FROM (SELECT k, bm25(chat_fts) AS s FROM chat_fts WHERE chat_fts MATCH $m ORDER BY s LIMIT 4) x
                JOIN chat ch ON ch.k=x.k ORDER BY x.s";
            cmd.Parameters.AddWithValue("$m", match);
            StringBuilder sb = new();
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read()) sb.AppendLine($"• [{r.GetString(0)}] {Clip(r.GetString(1), BodyCap)}");
            }
            return sb.Length == 0 ? $"no chat history matches \"{query}\".{LogGap(con, "recall", query)}" : Budget(sb.ToString());
        }
        catch (SqliteException)
        {
            return "chat history not indexed yet for this instance (run: aitm index-chat --from <transcript dir>).";
        }
    }

    [McpServerTool]
    [Description("Search the absorbed AI-meta docs (PRDs, plans, specs, audits) by topic. These were moved out of the repo into the foundation, chunked by section. Use to recall a requirement, design decision, or plan instead of grepping files.")]
    public static string doc(string query)
    {
        using SqliteConnection con = Open();
        string match = Match(query);
        if (match.Length == 0) return "no usable query terms.";
        try
        {
            using SqliteCommand cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT d.category,d.title,d.path,d.content
                FROM (SELECT k, bm25(docs_fts) AS s FROM docs_fts WHERE docs_fts MATCH $m ORDER BY s LIMIT 3) x
                JOIN docs d ON d.k=x.k ORDER BY x.s";
            cmd.Parameters.AddWithValue("$m", match);
            StringBuilder sb = new();
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                    sb.AppendLine($"• [{r.GetString(0)}] {r.GetString(1)} ({Clip(r.GetString(2), CellCap)})\n  {Clip(r.GetString(3), 280)}");
            }
            return sb.Length == 0 ? $"no docs match \"{query}\".{LogGap(con, "doc", query)}" : Budget(sb.ToString());
        }
        catch (SqliteException)
        {
            return "no docs indexed yet for this instance.";
        }
    }

    [McpServerTool]
    [Description("READ-ONLY recall of a standing RULE, preference, or past decision about HOW to work on this project — the migrated memory channel (feedback/user/project/reference), relevance-ranked. Use before writing code or making a call where the operator has likely already set a convention, instead of guessing or repeating a past correction. To CREATE or UPDATE a rule, do NOT use this — stage it with brain_stage then commit with brain_flush.")]
    public static string rule(string query)
    {
        using SqliteConnection con = Open();
        string match = Match(query);
        if (match.Length == 0) return "no usable query terms.";
        try
        {
            using SqliteCommand cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT mem.type,mem.hook,mem.body,mem.hard,mem.k
                FROM (SELECT k, bm25(memory_fts) AS s FROM memory_fts WHERE memory_fts MATCH $m ORDER BY s LIMIT 4) x
                JOIN memory mem ON mem.k=x.k ORDER BY x.s";
            cmd.Parameters.AddWithValue("$m", match);
            StringBuilder sb = new();
            List<string> hitKeys = new();
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    string tag = r.GetInt32(3) == 1 ? "HARD " : "";
                    hitKeys.Add(r.GetString(4));
                    sb.AppendLine($"• [{tag}{r.GetString(0)}] {r.GetString(1)}\n  {Clip(r.GetString(2), BodyCap)}");
                }
            }
            if (sb.Length == 0) return $"no rule matches \"{query}\".{LogGap(con, "rule", query)}";
            ReinforceChannel(con, "memory", hitKeys);
            return Budget(sb.ToString());
        }
        catch (SqliteException)
        {
            return "memory not indexed yet for this instance (run: aitm index-memory --from <memory dir>).";
        }
    }

    [McpServerTool]
    [Description("Forget one memory (a migrated RULE/preference/decision) by its exact key — deletes the row from the memory channel and logs the deletion to the cold trail. Use to retire a rule that no longer holds. The key is the slug shown by aitm's memory tooling; a wrong key is a no-op.")]
    public static string shed_memory(string key)
    {
        using SqliteConnection con = Open();
        using SqliteCommand snap = con.CreateCommand();
        snap.CommandText = "SELECT type||'|'||title||'|'||hook||'|'||body FROM memory WHERE k=$k";
        snap.Parameters.AddWithValue("$k", key);
        if (snap.ExecuteScalar() is not string before) return $"no memory '{key}'.";
        using SqliteCommand del = con.CreateCommand();
        del.CommandText = @"DELETE FROM memory_fts WHERE k=$k;
            DELETE FROM memory WHERE k=$k;
            INSERT INTO mutations(ts,op,kind,k,before,after,why)
              VALUES(strftime('%Y-%m-%dT%H:%M:%fZ','now'),'delete','memory',$k,$b,NULL,'shed_memory');";
        del.Parameters.AddWithValue("$k", key);
        del.Parameters.AddWithValue("$b", before);
        del.ExecuteNonQuery();
        return $"shed memory '{key}'.";
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
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = "SELECT ts,op,k FROM mutations WHERE k LIKE $t ORDER BY id DESC LIMIT 30";
        cmd.Parameters.AddWithValue("$t", "%" + term + "%");
        using SqliteDataReader r = cmd.ExecuteReader();
        StringBuilder sb = new();
        while (r.Read()) sb.AppendLine($"  {r.GetString(0)} {r.GetString(1)} {r.GetString(2)}");
        return sb.Length == 0 ? $"no history for '{term}'." : Budget(sb.ToString());
    }

    [McpServerTool]
    [Description("Log a FINDING: an unrelated bug or issue spotted during other work, to surface to the operator later (not now).")]
    public static string log_finding(string title, string detail = "", string source = "")
    {
        using SqliteConnection con = Open();
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO findings(ts,title,detail,source,status) VALUES($t,$ti,$d,$s,'open')";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$ti", title);
        cmd.Parameters.AddWithValue("$d", detail);
        cmd.Parameters.AddWithValue("$s", source);
        cmd.ExecuteNonQuery();
        return $"finding logged: {title}";
    }

    [McpServerTool]
    [Description("List open findings (unrelated issues spotted earlier) to surface to the operator.")]
    public static string open_findings()
    {
        using SqliteConnection con = Open();
        using SqliteCommand cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id,title,source FROM findings WHERE status='open' ORDER BY id DESC LIMIT 30";
        using SqliteDataReader r = cmd.ExecuteReader();
        StringBuilder sb = new();
        while (r.Read()) sb.AppendLine($"#{r.GetInt32(0)} {Clip(r.GetString(1), CellCap)} ({(r.IsDBNull(2) ? "" : Clip(r.GetString(2), 60))})");
        return sb.Length == 0 ? "no open findings." : Budget(sb.ToString());
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
            if (res.StartsWith("rejected", StringComparison.Ordinal) || res.StartsWith("unknown", StringComparison.Ordinal) || res.StartsWith("skip", StringComparison.Ordinal))
                rejects.AppendLine($"  ! {line} -> {res}");
            else
                n++;
        }
        File.Delete(ledger);
        return $"flushed {n} learning(s) into the brain." + (rejects.Length > 0 ? "\n" + rejects : "");
    }
}
