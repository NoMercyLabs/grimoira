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
#:project src/Aitm.Graph/Aitm.Graph.csproj
#:project src/Aitm.Brain/Aitm.Brain.csproj
#:project src/Aitm.Server/Aitm.Server.csproj
// AITM MCP server: exposes the per-instance store (knowledge, the cross-project impact graph,
// history, findings) as tools the agent calls every session. stdio transport; all host logging
// disabled so only tool output reaches the client.
// Instance resolves from AITM_INSTANCE, else the project dir (CLAUDE_PROJECT_DIR or cwd) basename,
// so the one user-scope server serves whatever repo the session runs in; store at ~/.aitm/<instance>/aitm.db.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
using Aitm.Graph.Tools;
using Aitm.Brain.Data;
using Aitm.Brain.Tools;
using Aitm.Server.Handover;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

[McpServerToolType]
public static class AitmTools
{
    private static SqliteConnection Open()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm", ResolveInstance(), "aitm.db");
        // brain_stage and brain_flush now route through this Open() too (RESTRUCTURE.md slice 24, MCP
        // part 2), and unlike the other tools here neither ever required an already-`init`-ed instance
        // before: brain_stage is pure ledger-file append, and old mcp.cs's own inline brain_stage
        // explicitly created this same directory itself for exactly that reason ("staging into a fresh
        // instance threw, and the MCP layer reported only 'An error occurred'"). SqliteConnection does
        // not create a missing parent directory, so a brand-new instance's first call — through any
        // tool — must not throw that same opaque error.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        return new ImpactTool().ExecuteMcp(con, symbol);
    }

    [McpServerTool]
    [Description("Find existing NoMercy tools by purpose, such as browser login, native code search, CI watching, or process ownership. Returns a few source paths with reviewed prerequisites and limitations when available. Read-only discovery: does not execute the found tools or access credentials. Coverage is explicit; a missing result is not proof that no tool exists.")]
    public static string workspace_capabilities(string query)
    {
        string root = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? Directory.GetCurrentDirectory();
        return new WorkspaceCapabilitiesTool().Execute(query, root, new ProcessRunner());
    }

    [McpServerTool]
    [Description("Search fixed text or filenames in one registered NoMercy repository. Requires an explicit repository path from the registry; '.' means the root. Returns file and line locations only, excludes common credential paths, and reports limits as incomplete coverage. Set names to true for filenames and path to narrow within the repository.")]
    public static string workspace_search(string repository, string pattern, string path = "", bool names = false)
    {
        string root = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? Directory.GetCurrentDirectory();
        return new WorkspaceSearchTool().Execute(repository, pattern, path, names, root, new ProcessRunner());
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
                + "raw token, use the workspace login drivers (C:/Projects/NoMercy/.claude/work/tools/idp).";
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

    [McpServerTool]
    [Description("BRAIN/always-on: the hard=1 nodes that apply every turn (the ~20-row replacement for the always-loaded MEMORY.md). Pull at the START of a session/turn before doing project work. No args.")]
    public static string brain_core()
    {
        using SqliteConnection con = Open();
        return new BrainCoreTool().ExecuteMcp(con);
    }

    [McpServerTool]
    [Description("BRAIN/scope: everything in scope for working across the named projects — shared seams/contracts/rules floated to top with DIRECTION (who exposes vs who consumes), each side's specifics below. One query. Use before any cross-project task. Args: projects = space/comma-separated keys or short aliases (web, android, server, ios).")]
    public static string brain_scope(string projects)
    {
        using SqliteConnection con = Open();
        return new BrainScopeTool().ExecuteMcp(con, projects);
    }

    [McpServerTool]
    [Description("BRAIN/intersection: what the named projects have in COMMON. Coverage-tolerant — returns shared_by + present_n + an unresolved list, so near-misses surface and a typo'd project is echoed, not silently dropped. One query. Args: projects = 2+ keys/aliases.")]
    public static string brain_common(string projects)
    {
        using SqliteConnection con = Open();
        return new BrainCommonTool().ExecuteMcp(con, projects);
    }

    [McpServerTool]
    [Description("BRAIN/placement: where new code of a kind belongs AND how it's written here, inheriting project/layer conventions up the broader chain (nearest wins) plus belongs_in and forbidden antipatterns. One recursive query. Call BEFORE writing new code so it's right-place/right-style first try. Args: codekind (e.g. vue-component, dotnet-api-endpoint, kotlin-compose-screen).")]
    public static string brain_place(string codekind)
    {
        using SqliteConnection con = Open();
        return new BrainPlaceTool().ExecuteMcp(con, codekind);
    }

    [McpServerTool]
    [Description("BRAIN/recall: a natural-language question -> DB-side synonym expansion -> FTS seed -> same-statement traversal to who-uses-it + the governing rule + the ground-truth fact. The catch-all when the question isn't clearly scope/common/place. One query. Args: query.")]
    public static string brain_recall(string query)
    {
        using SqliteConnection con = Open();
        return new BrainRecallTool().ExecuteMcp(con, query);
    }

    [McpServerTool]
    [Description("BRAIN/impact: every cross-project consumer of a shared symbol/contract (project/file/line/hardcoded) from the live edges graph plus the summary triple. Call BEFORE changing any shared contract. Args: symbol_or_contract (e.g. device_id, envelope, has_more).")]
    public static string brain_impact(string symbol)
    {
        using SqliteConnection con = Open();
        return new BrainImpactTool().ExecuteMcp(con, symbol);
    }

    [McpServerTool]
    [Description("BRAIN/write-last: persist a durable node, triple, or slot learned this turn (the continuous-update half of the protocol). Use AGGRESSIVELY: any new durable fact, correction, convention, contract, URL, or path you encounter is worth a write — writes are cheap, re-deriving is not, and a matching open gap auto-resolves. kind='node' -> key, a=nodekind, b=label, c=gloss; kind='triple' -> key=subject, a=predicate, b=object; kind='slot' -> key=frame, a=name, b=value. o_is_literal is derived; triggers reject typo'd predicates and dangling refs. Put anecdote/why in `because`, never the gloss.")]
    public static string brain_learn(string kind, string key, string a = "", string b = "", string c = "", string because = "", bool hard = false)
    {
        using SqliteConnection con = Open();
        return new BrainLearnTool().ExecuteMcp(con, kind, key, a, b, c, because, hard);
    }

    [McpServerTool]
    [Description("BRAIN/gaps: what the brain could NOT answer — every refused fact/rule/recall/doc/place query is auto-logged here. Check at session start or before research: an open gap you can now answer should be staged via brain_stage (a matching learn auto-resolves it). No args.")]
    public static string brain_gaps()
    {
        using SqliteConnection con = Open();
        return new BrainGapsTool().ExecuteMcp(con);
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

    [McpServerTool]
    [Description("BRAIN/write-side brake: STAGE a durable learning the instant you notice it (a cheap append, no DB write) — same args as brain_learn. The Stop hook refuses to end the turn while anything staged is unflushed, so nothing you stage is ever silently dropped (the rot that killed MEMORY.md). Stage AGGRESSIVELY — the MOMENT the operator corrects you, a lookup comes back wrong/empty, or a new ground-truth fact/convention/contract appears; when in doubt, stage it. Don't defer to turn-end and forget. Commit the batch with brain_flush.")]
    public static string brain_stage(string kind, string key, string a = "", string b = "", string c = "", string because = "", bool hard = false)
    {
        using SqliteConnection con = Open();
        return new BrainStageTool().ExecuteMcp(con, kind, key, a, b, c, because, hard);
    }

    [McpServerTool]
    [Description("BRAIN/write-side brake: COMMIT every staged learning into the brain in one pass, then clear the ledger so the Stop hook releases. Re-uses brain_learn for each, so supersession / conf-reinforcement / predicate + dangling-ref guards apply identically. Call at turn end (or any time) to persist what you staged.")]
    public static string brain_flush()
    {
        using SqliteConnection con = Open();
        return new BrainFlushTool().ExecuteMcp(con);
    }

    // --- Code graph: graph_query / graph_path / graph_explain -------------------------------------
    // All three read the SAME `edges` table `impact` reads: each row is a (symbol, file, line, project)
    // fact, either a declaration (contract='decl') or a curated usage site. There is no separate
    // call-graph table, so "defined-in" and "uses" are both read off this one table.

    [McpServerTool]
    [Description("Find the most relevant symbols, files and docs for a natural-language question, grouped by project, with one hop of neighbors (declaration file, top consuming projects). No LLM — word-boundary symbol matching plus the existing docs_fts lookup. Use to orient in an unfamiliar area of the codebase before making a change.")]
    public static string graph_query(string question)
    {
        using SqliteConnection con = Open();
        return new GraphQueryTool().ExecuteMcp(con, question);
    }

    [McpServerTool]
    [Description("Shortest path between two symbols or files over the code graph (declaration + usage edges), max depth 6, each hop printed with file:line. Use to answer \"how does A connect to B\" without tracing declaration-then-usage chains by hand.")]
    public static string graph_path(string a, string b)
    {
        using SqliteConnection con = Open();
        return new GraphPathTool().ExecuteMcp(con, a, b);
    }

    [McpServerTool]
    [Description("What a symbol IS (kind, definition file:line), who uses it (grouped by project, top 10 sites), and docs/rules that mention it. No call-graph data exists yet, so \"what it uses\" is reported as not tracked rather than guessed. Use before renaming or reasoning about a symbol you did not just write.")]
    public static string graph_explain(string symbol)
    {
        using SqliteConnection con = Open();
        return new GraphExplainTool().ExecuteMcp(con, symbol);
    }
}
