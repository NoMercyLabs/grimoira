#:package Microsoft.Data.Sqlite@10.0.9
#:package SQLitePCLRaw.bundle_e_sqlite3@3.0.3
// RESTRUCTURE.md slice 24, CLI lane part 1: aitm.cs's Store and Facts verbs now dispatch to their
// tool classes instead of carrying the logic inline.
// RESTRUCTURE.md slice 24, CLI lane part 2: the Memory and Docs verbs do the same.
// RESTRUCTURE.md slice 24, CLI lane part 3: aitm.cs's Graph verbs (project, projects, forget-project,
// extract-edges, candidates, promote, promote-all, seed-edges, impact, graph-query, graph-path,
// graph-explain) dispatch to their Aitm.Graph tool classes the same way.
// RESTRUCTURE.md slice 24, CLI lane part 4a: the Brain-read sub-verbs (core, scope, common, place, why,
// stale, gaps, verify, audit, export) do the same, nested inside BrainCmd's own switch.
// RESTRUCTURE.md slice 24, CLI lane part 4b: the Brain write verbs (learn, learn-batch, set-hard, merge,
// forget, unlink, shed-node, tidy, distill) do the same.
#:project src/Aitm.Store/Aitm.Store.csproj
#:project src/Aitm.Facts/Aitm.Facts.csproj
#:project src/Aitm.Memory/Aitm.Memory.csproj
#:project src/Aitm.Docs/Aitm.Docs.csproj
#:project src/Aitm.Graph/Aitm.Graph.csproj
#:project src/Aitm.Brain/Aitm.Brain.csproj
// AITM foundation — core slice: a per-instance SQLite store with a CURRENT projection
// (one row per entity, the only thing normal reads touch) plus an append-only MUTATIONS
// log (cold; read only for trace/rollback). FTS5-ranked knowledge lookup. Generic: the
// engine is project-agnostic, all behaviour driven by the per-instance store under ~/.aitm.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aitm.Brain.Tools;
using Aitm.Docs.Tools;
using Aitm.Facts.Tools;
using Aitm.Graph.Tools;
using Aitm.Memory.Tools;
using Aitm.Store.Data;
using Aitm.Store.Tools;
using Microsoft.Data.Sqlite;

string[] a = args;
string cmd = a.Length > 0 ? a[0] : "help";
string instance = GetFlag("--instance") ?? ResolveInstance();
string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm", instance);
Directory.CreateDirectory(root);
string dbPath = Path.Combine(root, "aitm.db");

using SqliteConnection db = new($"Data Source={dbPath};Foreign Keys=True");
db.Open();
// SQLite serialises writers, and Init/InitBrain below are DDL, so even `query` takes the write lock.
// Without a busy timeout a momentary writer (session indexer, MCP server, a second CLI) makes the
// command throw SQLITE_BUSY and the process hard-crash, so callers read the store as broken and fall
// back to grepping the tree. Wait for the writer instead of dying.
// The indexer can hold the write lock for minutes, and 5 seconds was not enough: `aitm add` died with
// "database is locked" mid-sentence while the SessionEnd doc indexer ran.
Exec("PRAGMA busy_timeout=30000");
// WAL is the actual cure: under the default rollback journal a single writer (the async SessionEnd
// doc indexer runs for minutes) blocks every reader outright, so concurrent CLI/MCP/hook reads died.
// No-op once the store is already WAL, and switching journal mode itself needs a lock — so a failure
// here means someone else is mid-write, which is not a reason to kill the command.
TryExec("PRAGMA journal_mode=WAL");

// Init and InitBrain are CREATE TABLE IF NOT EXISTS, which is DDL, which takes the WRITE lock — so
// every `query` and every hook lookup was contending with the indexer just to confirm a schema that
// had not changed. user_version is a header read with no lock, so the common case now touches nothing.
// BUMP THIS whenever the schema changes; otherwise an existing store never learns about the new table.
const int SchemaVersion = 3;
if (ScalarLong("PRAGMA user_version") != SchemaVersion)
{
    Init();
    InitBrain();
    Exec($"PRAGMA user_version={SchemaVersion}");
}

HashSet<string> stop = new(StringComparer.OrdinalIgnoreCase)
{
    "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are", "does",
    "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you", "we", "there",
    "was", "were", "which", "when", "where", "name", "called", "get", "got", "me", "us", "about",
};

// bm25 lower = better. Matches weaker than this are low-IDF noise (a generic single token like "server"
// hitting many facts) — refuse rather than surface them. Calibrated on real data: on-target queries
// score <= -8.6, generic single-token noise >= -2.2; -3.0 sits cleanly in the gap.
// Only trusted once the corpus is large enough for IDF to discriminate (below that, return the best match
// rather than over-refuse a young store).
const double RelevanceFloor = -3.0;
const int FloorMinCorpus = 20;

try
{
switch (cmd)
{
    case "init":
        Console.WriteLine(new InitTool().Execute(instance, dbPath));
        break;
    case "import":
        Console.WriteLine(new ImportTool().Execute(db,
            GetFlag("--from") ?? throw new ArgumentException("import needs --from <sqlite path>"), dbPath));
        break;
    case "query":
        Console.WriteLine(new QueryTool(new UsageSignal()).ExecuteCli(db, string.Join(' ', Positionals().Skip(1))));
        break;
    case "index-chat":
        Console.WriteLine(new IndexChatTool().Execute(db,
            GetFlag("--from") ?? throw new ArgumentException("index-chat needs --from <session.jsonl | transcript dir>")));
        break;
    case "index-packages":
        // --from is what every other index-* command takes; accepting only --root here silently
        // indexed the current directory instead of the one that was asked for.
        Console.WriteLine(new IndexPackagesTool().Execute(db, GetFlag("--root") ?? GetFlag("--from") ?? Directory.GetCurrentDirectory()));
        break;
    case "index-docs":
        Console.WriteLine(new IndexDocsTool().Execute(db,
            GetFlag("--from") ?? throw new ArgumentException("index-docs needs --from <dir|file.md>"), GetFlag("--category") ?? "doc"));
        break;
    case "doc":
        Console.WriteLine(new DocTool().ExecuteCli(db, string.Join(' ', Positionals().Skip(1))));
        break;
    case "index-memory":
        Console.WriteLine(new IndexMemoryTool().Execute(db,
            GetFlag("--from") ?? throw new ArgumentException("index-memory needs --from <memory dir>")));
        break;
    case "mem":
        Console.WriteLine(new MemTool(new UsageSignal()).ExecuteCli(db, string.Join(' ', Positionals().Skip(1)), a.Contains("--hard")));
        break;
    case "brain":
        BrainCmd(Positionals().Skip(1).ToList());
        break;
    case "shed-doc":
        Console.WriteLine(new ShedDocTool().Execute(db, GetFlag("--path") ?? throw new ArgumentException("shed-doc needs --path <substring>")));
        break;
    case "shed-synthesis":
        Console.WriteLine(new ShedSynthesisTool().Execute(db, GetFlag("--path") ?? throw new ArgumentException("shed-synthesis needs --path <source dir>")));
        break;
    case "add-synthesis":
        Console.WriteLine(new AddSynthesisTool().Execute(db,
            GetFlag("--path") ?? throw new ArgumentException("add-synthesis needs --path <source dir>"),
            GetFlag("--from") ?? throw new ArgumentException("add-synthesis needs --from <file>"),
            GetFlag("--title") ?? "",
            GetFlag("--sources") ?? ""));
        break;
    case "shed-memory":
        Console.WriteLine(new ShedMemoryTool().ExecuteCli(db, GetFlag("--key") ?? throw new ArgumentException("shed-memory needs --key <slug>")));
        break;
    case "shed-fact":
        Console.WriteLine(new ShedFactTool().Execute(db, GetFlag("--key") ?? throw new ArgumentException("shed-fact needs --key <term>")));
        break;
    case "recompact-docs":
        Console.WriteLine(new RecompactDocsTool().Execute(db));
        break;
    case "recall":
        Console.WriteLine(new RecallTool().ExecuteCli(db, string.Join(' ', Positionals().Skip(1))));
        break;
    case "redact-chat":
        Console.WriteLine(new RedactChatTool().Execute(db, root, a.Contains("--dry-run")));
        break;
    case "eval":
        Eval();
        break;
    case "add":
    {
        string term = GetFlag("--term") ?? throw new ArgumentException("add needs --term");
        string prov = (GetFlag("--provenance") ?? "unverified").ToLowerInvariant();
        Console.WriteLine(new AddTool().Execute(db, term, GetFlag("--aliases") ?? "[]", GetFlag("--category") ?? "manual",
            GetFlag("--value") ?? "", GetFlag("--source") ?? "", GetFlag("--notes") ?? "", prov, GetFlag("--why") ?? "manual"));
        break;
    }
    case "history":
        Console.WriteLine(new HistoryTool().ExecuteCli(db, string.Join(' ', Positionals().Skip(1))));
        break;
    case "seed-edges":
    {
        string seedPath = GetFlag("--from") ?? Path.Combine(AppContext.BaseDirectory, "..", "seeds", "spine.json");
        // SeedEdgesTool.Execute returns the same "no spine file at ..." text the old inline SeedEdges
        // wrote to stderr, so the missing-file case has to be checked here to keep it on stderr.
        if (!File.Exists(seedPath)) { Console.Error.WriteLine($"no spine file at {Path.GetFullPath(seedPath)}"); break; }
        Console.WriteLine(new SeedEdgesTool().Execute(db, seedPath));
        break;
    }
    case "spine-export":
        SpineExport(GetFlag("--to") ?? Path.Combine(AppContext.BaseDirectory, "..", "seeds", "spine.json"));
        break;
    // Same gap forget-project had: the graph could gain a node but never lose one, so anything indexed
    // by mistake or moved out of scope stayed live forever. Retires on the timeline rather than
    // deleting, because "indexed once, now out of scope" is a different fact from "never existed".
    case "shed-node":
    {
        string nk = GetFlag("--key") ?? throw new ArgumentException("shed-node needs --key");
        Console.WriteLine(new ShedNodeTool().Execute(db, root, nk));
        break;
    }
    case "spine-import":
        SpineImport(GetFlag("--from") ?? throw new ArgumentException("spine-import needs --from <spine.json>"));
        break;
    case "project":
        Console.WriteLine(new ProjectTool().Execute(db,
            GetFlag("--name") ?? throw new ArgumentException("project needs --name"),
            GetFlag("--root") ?? throw new ArgumentException("project needs --root"),
            GetFlag("--lang") ?? "", GetFlag("--globs") ?? "*.ts,*.tsx,*.vue,*.kt,*.cs"));
        break;
    case "projects":
        Console.WriteLine(new ProjectsTool().Execute(db));
        break;
    // Registration without deregistration left roots that no longer exist on disk still answering
    // questions with paths that are gone, which is worse than not knowing.
    case "forget-project":
        Console.WriteLine(new ForgetProjectTool().Execute(db, root,
            GetFlag("--name") ?? throw new ArgumentException("forget-project needs --name")));
        break;
    case "extract-edges":
        Console.WriteLine(new ExtractEdgesTool().Execute(db,
            GetFlag("--symbol") ?? throw new ArgumentException("extract-edges needs --symbol"), GetFlag("--contract") ?? ""));
        break;
    case "candidates":
        Console.WriteLine(new CandidatesTool().Execute(db, GetFlag("--symbol")));
        break;
    case "promote":
        Console.WriteLine(new PromoteTool().Execute(db, int.Parse(Pos1(), CultureInfo.InvariantCulture)));
        break;
    case "promote-all":
        Console.WriteLine(new PromoteAllTool().Execute(db, root,
            GetFlag("--symbol") ?? throw new ArgumentException("promote-all needs --symbol")));
        break;
    case "impact":
        Console.WriteLine(new ImpactTool().ExecuteCli(db, string.Join(' ', Positionals().Skip(1))));
        break;
    case "graph-query":
        Console.WriteLine(new GraphQueryTool().ExecuteCli(db, string.Join(' ', Positionals().Skip(1))));
        break;
    case "graph-path":
    {
        List<string> gp = Positionals().Skip(1).ToList();
        if (gp.Count < 2) { Console.WriteLine("usage: aitm graph-path <A> <B>"); break; }
        Console.WriteLine(new GraphPathTool().ExecuteCli(db, gp[0], gp[1]));
        break;
    }
    case "graph-explain":
        Console.WriteLine(new GraphExplainTool().ExecuteCli(db, Pos1()));
        break;
    case "todo":
        Console.WriteLine(new TodoTool().Execute(db, GetFlag("--title") ?? Pos1(), GetFlag("--why") ?? ""));
        break;
    case "todos":
        Console.WriteLine(new TodosTool().Execute(db));
        break;
    case "done":
        Console.WriteLine(new DoneTool().Execute(db, int.Parse(Pos1(), CultureInfo.InvariantCulture)));
        break;
    case "finding":
        Console.WriteLine(new FindingTool().ExecuteCli(db, GetFlag("--title") ?? Pos1(), GetFlag("--detail") ?? "", GetFlag("--source") ?? ""));
        break;
    case "findings":
        Console.WriteLine(new FindingsTool().ExecuteCli(db));
        break;
    case "resolve":
        Console.WriteLine(new ResolveTool().Execute(db, int.Parse(Pos1(), CultureInfo.InvariantCulture)));
        break;
    case "stats":
        Console.WriteLine(new StatsTool().Execute(db, instance, dbPath));
        break;
    case "backup":
        Console.WriteLine(new BackupTool().Execute(db, root, GetFlag("--to")));
        break;
    case "loop":
        // Removed in phase 2 (RESTRUCTURE.md section 2.1, drop 4 of 4): loop only ever wrote
        // loop-state.json for loop-guard.mjs and session-continue.mjs, both dropped in the same
        // pass; the NoMercy task record's stop-check does this job now. start/tick/clear existed
        // only as loop's own sub-verbs, so this one message covers all three call shapes.
        Console.Error.WriteLine("error: removed in 0.4: aitm loop is gone; the NoMercy task record replaces it.\n");
        Environment.Exit(2);
        break;
    case "stage":
        StageCmd(Positionals().Skip(1).ToList());
        break;
    case "flush":
        FlushCmd();
        break;
    case "selftest":
        SelfTest();
        break;
    default:
        // One command per line so adding/removing a command is a one-line diff, not a rewrite of the whole string.
        string[] usage =
        {
            "init                                create/open the instance",
            "import --from <db>                  merge another aitm.db into this one",
            "add [--provenance stated|inferred]  add a fact (provenance: who established it)",
            "query <terms>                       look up a verified fact",
            "recall <terms>                      search past chat history",
            "index-chat --from <path>            ingest session transcript(s) into chat",
            "index-packages [--root <dir>]       index package.json identities",
            "index-docs --from <dir>             absorb AI-meta docs, chunked by section",
            "doc <terms>                         search absorbed docs",
            "shed-doc --path <s>                 drop absorbed doc sections by path",
            "add-synthesis --path <dir> --from <f>  file an answer distilled from a source set",
            "shed-memory --key <slug>            forget one memory by key",
            "shed-fact --key <term>             forget one fact by key (its term)",
            "index-memory --from <dir>           migrate MEMORY.md files into the memory channel",
            "mem <terms> | mem --hard            recall rules; --hard lists the always-on core",
            "eval                                run the retrieval eval set",
            "history <term>                      show the cold mutation log for an entity",
            "seed-edges                          sync the curated cross-project edge seed",
     "spine-export [--to <f>]             dump the curated spine to JSON",
     "spine-import --from <f>             load a curated spine from JSON",
     "shed-node --key <k>                 retire a node and its links",
            "project --name <n> --root <dir>     register a project root",
            "projects                            list registered projects",
     "forget-project --name <n>           unregister a project and drop its edges",
            "extract-edges --symbol <s>          grep edge candidates for a symbol",
            "candidates [--symbol <s>]           list edge candidates",
            "promote <id>                        promote one candidate into the graph",
            "promote-all --symbol <s>            authoritative replace of a symbol's edges",
            "impact <symbol>                     show consumers + contract sites of a symbol",
            "graph-query <question>              relevant symbols/files/docs for a question, 1-hop neighbors",
            "graph-path <A> <B>                  shortest code-graph path between two symbols/files (depth <=6)",
            "graph-explain <symbol>              what a symbol is, who uses it, docs/rules that mention it",
            "todo | todos | done <id>            manage todos",
            "finding | findings | resolve <id>   manage findings",
            "stats                               channel counts for the instance",
            "selftest                            run the TDD assertion harness (test* only)",
        };
        string usageText = "aitm <command> [--instance <name>]\n\n" + string.Join("\n", usage);
        if (cmd is "help" or "--help" or "-h")
        {
            Console.WriteLine(usageText);
            break;
        }
        // A caller that gets the help text back with exit 0 reads it as success while nothing ran.
        Console.Error.WriteLine(cmd.StartsWith("-")
            ? $"error: unknown command '{cmd}': the command comes first, flags go after the command.\n"
            : $"error: unknown command '{cmd}'.\n");
        Console.Error.WriteLine(usageText);
        Environment.Exit(2);
        break;
}
}
catch (ArgumentException argEx)
{
    Console.Error.WriteLine($"error: {argEx.Message}");
    Environment.Exit(2);
}
catch (FormatException)
{
    Console.Error.WriteLine("error: expected a numeric id (e.g. aitm done 3).");
    Environment.Exit(2);
}

string? GetFlag(string name)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

// Generic instance resolution (explicit --instance already won at the call site): AITM_INSTANCE,
// else the project dir (CLAUDE_PROJECT_DIR or cwd) basename — the same binary serves any repo, no config.
string ResolveInstance()
{
    string? env = Environment.GetEnvironmentVariable("AITM_INSTANCE");
    if (!string.IsNullOrWhiteSpace(env)) return Slug(env);
    string? proj = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR");
    string dir = string.IsNullOrWhiteSpace(proj) ? Directory.GetCurrentDirectory() : proj;
    string name = Path.GetFileName(dir.TrimEnd('/', '\\'));
    return string.IsNullOrWhiteSpace(name) ? "default" : Slug(name);
}

string Slug(string text) => new(text.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());

// Positional args only — drops every "--flag" AND the value following it (so a flag value never leaks into query terms).
List<string> Positionals()
{
    List<string> p = new();
    for (int i = 0; i < a.Length; i++)
    {
        // A flag only consumes the next token as its value when that token isn't itself a flag — so a boolean
        // flag (--hard, --multi) followed by another flag or at the end doesn't swallow an unrelated positional.
        if (a[i].StartsWith("--")) { if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) i++; continue; }
        p.Add(a[i]);
    }
    return p;
}

void Exec(string sql)
{
    using SqliteCommand c = db.CreateCommand();
    c.CommandText = sql;
    c.ExecuteNonQuery();
}

// Run a statement that is allowed to fail benignly (idempotent migrations like ADD COLUMN on an existing column).
void TryExec(string sql)
{
    try { Exec(sql); }
    catch (SqliteException) { }
}

void Run(string sql, params (string name, object? val)[] ps)
{
    using SqliteCommand c = db.CreateCommand();
    c.CommandText = sql;
    foreach ((string name, object? val) in ps) c.Parameters.AddWithValue(name, val ?? DBNull.Value);
    c.ExecuteNonQuery();
}

void Init()
{
    Exec("CREATE TABLE IF NOT EXISTS facts(k TEXT PRIMARY KEY, term TEXT, aliases TEXT, category TEXT, value TEXT, source TEXT, notes TEXT);");
    // Whether a stored reason was STATED by the operator or INFERRED by the agent decides how much
    // weight it carries when the question is "may I change this". A track record that cannot tell its
    // own conclusions from the operator's is just a confident guess with citations.
    TryExec("ALTER TABLE facts ADD COLUMN provenance TEXT NOT NULL DEFAULT 'unverified';");
    Exec("CREATE TABLE IF NOT EXISTS mutations(id INTEGER PRIMARY KEY, ts TEXT, op TEXT, kind TEXT, k TEXT, before TEXT, after TEXT, why TEXT);");
    Exec("CREATE VIRTUAL TABLE IF NOT EXISTS facts_fts USING fts5(k UNINDEXED, term, aliases, category, value, notes);");
    Exec("CREATE TABLE IF NOT EXISTS edges(id INTEGER PRIMARY KEY, symbol TEXT, contract TEXT, project TEXT, file TEXT, line INTEGER, usage TEXT, hardcoded INTEGER);");
    Exec("CREATE TABLE IF NOT EXISTS todos(id INTEGER PRIMARY KEY, ts TEXT, title TEXT, status TEXT, why TEXT);");
    Exec("CREATE TABLE IF NOT EXISTS findings(id INTEGER PRIMARY KEY, ts TEXT, title TEXT, detail TEXT, source TEXT, status TEXT);");
    // Chat history is its own channel so transcript noise never pollutes the verified facts lookup.
    Exec("CREATE TABLE IF NOT EXISTS chat(k TEXT PRIMARY KEY, session TEXT, ts TEXT, role TEXT, text TEXT);");
    Exec("CREATE VIRTUAL TABLE IF NOT EXISTS chat_fts USING fts5(k UNINDEXED, text);");
    // Stored-driven project roots + a candidate staging area so extracted edges are reviewed
    // before they enter the curated graph (grep noise never pollutes impact()).
    Exec("CREATE TABLE IF NOT EXISTS projects(name TEXT PRIMARY KEY, root TEXT, lang TEXT, globs TEXT);");
    Exec("CREATE TABLE IF NOT EXISTS edge_candidates(id INTEGER PRIMARY KEY, symbol TEXT, contract TEXT, project TEXT, file TEXT, line INTEGER, usage TEXT, hardcoded INTEGER, status TEXT);");
    // Docs channel: AI-meta docs (plans/specs/prd/audits) absorbed out of the repo, chunked by markdown
    // heading so recall returns the relevant section, not a whole-file blob. Its own channel like chat.
    Exec("CREATE TABLE IF NOT EXISTS docs(k TEXT PRIMARY KEY, path TEXT, title TEXT, category TEXT, content TEXT, terms TEXT);");
    TryExec("ALTER TABLE docs ADD COLUMN terms TEXT"); // migrate stores created before path terms were searchable
    Exec("CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts USING fts5(k UNINDEXED, title, content);");
    // Memory channel: the durable rules/preferences/decisions that govern how the agent works (migrated out of the
    // always-loaded MEMORY.md index). hook is the high-signal one-liner; hard flags an always-on rule that the
    // thin MEMORY.md core still carries. Retrieval is pull-based so the bulk no longer costs per-session context.
    Exec("CREATE TABLE IF NOT EXISTS memory(k TEXT PRIMARY KEY, type TEXT, title TEXT, hook TEXT, body TEXT, links TEXT, hard INTEGER);");
    Exec("CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(k UNINDEXED, title, hook, body);");
    // Gap channel: every refused/empty lookup is recorded so the store knows what it does NOT know;
    // a later learn whose text covers a gap's tokens auto-resolves it. meta carries maintenance
    // bookkeeping. Shared DDL with the MCP server (mcp.cs Open()) — keep in lockstep.
    Exec("CREATE TABLE IF NOT EXISTS gaps(id INTEGER PRIMARY KEY, query TEXT NOT NULL UNIQUE, tool TEXT NOT NULL, misses INTEGER NOT NULL DEFAULT 1, first_ts TEXT NOT NULL, last_ts TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'open');");
    Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);");
}

void LogGap(string tool, string query)
{
    string norm = string.Join(' ', Tokens(query));
    if (norm.Length < 3) return;
    Run(@"INSERT INTO gaps(query,tool,misses,first_ts,last_ts) VALUES($q,$t,1,$ts,$ts)
        ON CONFLICT(query) DO UPDATE SET misses=misses+1, last_ts=$ts, status='open', tool=$t",
        ("$q", norm), ("$t", tool), ("$ts", Now()));
}

string NodeGuard(string kind, string label)
{
    if (!Regex.IsMatch(kind, "^[a-z0-9_-]{2,30}$"))
        return "rejected: node kind must be a short lowercase vocab token (rule/fact/concept/symbol/finding/contract/seam/codekind/project/reference/platform/layer). Yours looks like prose — order is <k> <kind> <label>, the statement goes in --gloss.";
    if (label.Length > 120)
        return "rejected: label must be a short noun phrase (max 120 chars) — the full statement belongs in --gloss.";
    return "";
}

void ResolveGaps(string learnedText)
{
    string hay = learnedText.ToLowerInvariant();
    List<long> filled = new();
    using (SqliteCommand c = db.CreateCommand())
    {
        c.CommandText = "SELECT id, query FROM gaps WHERE status='open' LIMIT 200";
        using SqliteDataReader r = c.ExecuteReader();
        while (r.Read())
        {
            if (r.GetString(1).Split(' ').All(t => hay.Contains(t, StringComparison.Ordinal))) filled.Add(r.GetInt64(0));
        }
    }
    foreach (long id in filled)
        Run("UPDATE gaps SET status='filled', last_ts=$ts WHERE id=$i", ("$i", id.ToString(CultureInfo.InvariantCulture)), ("$ts", Now()));
}

// BRAIN v2 — a structured copy of the operator's mental model: a uniform (s,p,o) triple graph over typed
// nodes, with a placement/convention slot overlay and a ref bridge into the existing channels. Bi-temporal
// (supersede, never delete). Predicate vocabulary + link-object existence are enforced by TRIGGERS (which
// fire regardless of the per-connection foreign_keys pragma) so a typo'd relation is a write error, not a
// silently broken graph. Designed + SQL-verified by the aitm-brain-schema workflow. Runs after Init() —
// the ref trigger and legacy_consumes view reference the base channels Init() creates.
void InitBrain()
{
    Exec("""
    CREATE TABLE IF NOT EXISTS pred_vocab (
      p TEXT PRIMARY KEY, inverse TEXT, is_link INTEGER NOT NULL DEFAULT 1,
      transitive INTEGER NOT NULL DEFAULT 0, is_sharing INTEGER NOT NULL DEFAULT 0,
      conflicts TEXT, note TEXT
    );
    INSERT OR IGNORE INTO pred_vocab(p,inverse,is_link,transitive,is_sharing,conflicts,note) VALUES
     ('consumes','consumed_by',1,0,1,NULL,'project/codekind depends on a seam/contract'),
     ('exposes','exposed_by',1,0,0,NULL,'producer side; not a shared dependency by itself'),
     ('implements','implemented_by',1,0,1,NULL,'consumer satisfies a contract it does not own'),
     ('governed_by','governs',1,0,1,'forbids','subject bound by a convention/rule/design system'),
     ('requires','required_by',1,0,1,'forbids','hard prerequisite'),
     ('broader','narrower',1,1,0,NULL,'SKOS scope/placement hierarchy (transitive)'),
     ('part_of','has_part',1,1,0,NULL,'composition hierarchy (transitive)'),
     ('related','related',1,0,0,NULL,'associative cross-link (was memory.links)'),
     ('shares_with','shares_with',1,0,0,NULL,'explicit project<->project shared seam shortcut'),
     ('belongs_in','contains_kind',1,0,0,NULL,'a codekind lives in a project/layer'),
     ('forbids','forbidden_in',1,0,0,'governed_by','rule bans a pattern for this subject');

    CREATE TABLE IF NOT EXISTS node (
      id INTEGER PRIMARY KEY, k TEXT NOT NULL, kind TEXT NOT NULL, label TEXT NOT NULL,
      gloss TEXT DEFAULT '', scheme TEXT, props TEXT NOT NULL DEFAULT '{}', hard INTEGER NOT NULL DEFAULT 0,
      valid_from TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), valid_to TEXT,
      superseded_by INTEGER REFERENCES node(id)
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_node_live ON node(k) WHERE valid_to IS NULL;
    CREATE INDEX IF NOT EXISTS ix_node_kind ON node(kind) WHERE valid_to IS NULL;
    CREATE INDEX IF NOT EXISTS ix_node_scheme ON node(scheme) WHERE valid_to IS NULL;
    CREATE INDEX IF NOT EXISTS ix_node_hard ON node(hard) WHERE valid_to IS NULL AND hard = 1;

    CREATE TABLE IF NOT EXISTS triple (
      id INTEGER PRIMARY KEY, s TEXT NOT NULL, p TEXT NOT NULL REFERENCES pred_vocab(p), o TEXT NOT NULL,
      o_is_literal INTEGER NOT NULL DEFAULT 0, ov TEXT, because TEXT DEFAULT '', conf REAL NOT NULL DEFAULT 1.0,
      hard INTEGER NOT NULL DEFAULT 0, src TEXT,
      valid_from TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), valid_to TEXT,
      superseded_by INTEGER REFERENCES triple(id)
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_triple_live ON triple(s,p,o) WHERE valid_to IS NULL;
    CREATE INDEX IF NOT EXISTS ix_triple_spo ON triple(s,p,o) WHERE valid_to IS NULL;
    CREATE INDEX IF NOT EXISTS ix_triple_pos ON triple(p,o,s) WHERE valid_to IS NULL;
    CREATE INDEX IF NOT EXISTS ix_triple_osp ON triple(o,s) WHERE valid_to IS NULL AND o_is_literal=0;
    CREATE INDEX IF NOT EXISTS ix_triple_hard ON triple(s,p) WHERE valid_to IS NULL AND hard=1;

    CREATE TRIGGER IF NOT EXISTS triple_pred_guard_ins BEFORE INSERT ON triple
    WHEN NEW.p NOT IN (SELECT p FROM pred_vocab) BEGIN SELECT RAISE(ABORT,'unknown predicate'); END;
    CREATE TRIGGER IF NOT EXISTS triple_pred_guard_upd BEFORE UPDATE OF p ON triple
    WHEN NEW.p NOT IN (SELECT p FROM pred_vocab) BEGIN SELECT RAISE(ABORT,'unknown predicate'); END;
    CREATE TRIGGER IF NOT EXISTS triple_litflag_ins BEFORE INSERT ON triple
    WHEN NEW.o_is_literal != (SELECT 1 - is_link FROM pred_vocab WHERE p = NEW.p) BEGIN SELECT RAISE(ABORT,'o_is_literal must match pred_vocab.is_link'); END;
    CREATE TRIGGER IF NOT EXISTS triple_litflag_upd BEFORE UPDATE OF o,o_is_literal,p ON triple
    WHEN NEW.o_is_literal != (SELECT 1 - is_link FROM pred_vocab WHERE p = NEW.p) BEGIN SELECT RAISE(ABORT,'o_is_literal must match pred_vocab.is_link'); END;
    CREATE TRIGGER IF NOT EXISTS triple_obj_fk_ins BEFORE INSERT ON triple
    WHEN NEW.o_is_literal = 0 AND NOT EXISTS(SELECT 1 FROM node WHERE k = NEW.o AND valid_to IS NULL) BEGIN SELECT RAISE(ABORT,'dangling link object'); END;
    CREATE TRIGGER IF NOT EXISTS triple_obj_fk_upd BEFORE UPDATE OF o,o_is_literal ON triple
    WHEN NEW.o_is_literal = 0 AND NOT EXISTS(SELECT 1 FROM node WHERE k = NEW.o AND valid_to IS NULL) BEGIN SELECT RAISE(ABORT,'dangling link object'); END;
    CREATE TRIGGER IF NOT EXISTS triple_subj_fk_ins BEFORE INSERT ON triple
    WHEN NOT EXISTS(SELECT 1 FROM node WHERE k = NEW.s AND valid_to IS NULL) BEGIN SELECT RAISE(ABORT,'dangling subject'); END;

    CREATE TABLE IF NOT EXISTS slot (
      id INTEGER PRIMARY KEY, frame_k TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL,
      facet TEXT DEFAULT 'text', multi INTEGER NOT NULL DEFAULT 0, because TEXT DEFAULT '', src TEXT DEFAULT '',
      valid_from TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), valid_to TEXT,
      superseded_by INTEGER REFERENCES slot(id)
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_slot_single ON slot(frame_k,name) WHERE valid_to IS NULL AND multi = 0;
    CREATE UNIQUE INDEX IF NOT EXISTS ux_slot_multi ON slot(frame_k,name,value) WHERE valid_to IS NULL AND multi = 1;
    CREATE INDEX IF NOT EXISTS ix_slot_name ON slot(name) WHERE valid_to IS NULL;
    CREATE TRIGGER IF NOT EXISTS slot_frame_fk_ins BEFORE INSERT ON slot
    WHEN NOT EXISTS(SELECT 1 FROM node WHERE k = NEW.frame_k AND valid_to IS NULL) BEGIN SELECT RAISE(ABORT,'dangling slot frame'); END;

    CREATE TABLE IF NOT EXISTS ref (
      node_k TEXT NOT NULL, channel TEXT NOT NULL CHECK (channel IN ('facts','memory','docs','chat','edges')),
      payload_k TEXT NOT NULL, role TEXT DEFAULT '', PRIMARY KEY (node_k, channel, payload_k)
    );
    CREATE INDEX IF NOT EXISTS ix_ref_payload ON ref(channel, payload_k);
    CREATE INDEX IF NOT EXISTS ix_ref_node ON ref(node_k);
    CREATE TRIGGER IF NOT EXISTS ref_fk_ins BEFORE INSERT ON ref BEGIN
      SELECT CASE
        WHEN NOT EXISTS(SELECT 1 FROM node WHERE k=NEW.node_k AND valid_to IS NULL) THEN RAISE(ABORT,'ref.node_k not a live node')
        WHEN NEW.channel='facts' AND NOT EXISTS(SELECT 1 FROM facts WHERE k=NEW.payload_k) THEN RAISE(ABORT,'ref payload missing in facts')
        WHEN NEW.channel='memory' AND NOT EXISTS(SELECT 1 FROM memory WHERE k=NEW.payload_k) THEN RAISE(ABORT,'ref payload missing in memory')
        WHEN NEW.channel='docs' AND NOT EXISTS(SELECT 1 FROM docs WHERE k=NEW.payload_k) THEN RAISE(ABORT,'ref payload missing in docs')
        WHEN NEW.channel='chat' AND NOT EXISTS(SELECT 1 FROM chat WHERE k=NEW.payload_k) THEN RAISE(ABORT,'ref payload missing in chat')
        WHEN NEW.channel='edges' AND NOT EXISTS(SELECT 1 FROM edges WHERE id=CAST(NEW.payload_k AS INTEGER)) THEN RAISE(ABORT,'ref payload missing in edges')
      END;
    END;

    CREATE TABLE IF NOT EXISTS term_alias (term TEXT NOT NULL, canonical TEXT NOT NULL, PRIMARY KEY (term, canonical));
    -- Only aliases that hold for any codebase ship built in. Everything that names a specific
    -- technology choice (which identity provider, which realtime transport, which design system) is
    -- one ecosystem's vocabulary and arrives through spine-import instead.
    INSERT OR IGNORE INTO term_alias(term,canonical) VALUES
     ('authentication','auth'),('signin','auth'),('login','auth'),
     ('localization','i18n'),('translation','i18n'),('accessibility','a11y'),
     ('endpoint','api'),('rest','api'),('controller','api'),('dto','contract'),('placement','belongs_in');

    CREATE VIRTUAL TABLE IF NOT EXISTS node_fts USING fts5(k UNINDEXED, label, gloss, props, content='node', content_rowid='id', tokenize='porter unicode61');
    CREATE TRIGGER IF NOT EXISTS node_ai AFTER INSERT ON node BEGIN INSERT INTO node_fts(rowid,k,label,gloss,props) VALUES(new.id,new.k,new.label,new.gloss,new.props); END;
    CREATE TRIGGER IF NOT EXISTS node_ad AFTER DELETE ON node BEGIN INSERT INTO node_fts(node_fts,rowid,k,label,gloss,props) VALUES('delete',old.id,old.k,old.label,old.gloss,old.props); END;
    CREATE TRIGGER IF NOT EXISTS node_au AFTER UPDATE ON node BEGIN
      INSERT INTO node_fts(node_fts,rowid,k,label,gloss,props) VALUES('delete',old.id,old.k,old.label,old.gloss,old.props);
      INSERT INTO node_fts(rowid,k,label,gloss,props) VALUES(new.id,new.k,new.label,new.gloss,new.props);
    END;

    CREATE VIRTUAL TABLE IF NOT EXISTS slot_fts USING fts5(frame_k UNINDEXED, name, value, because, content='slot', content_rowid='id', tokenize='porter unicode61');
    CREATE TRIGGER IF NOT EXISTS slot_ai AFTER INSERT ON slot BEGIN INSERT INTO slot_fts(rowid,frame_k,name,value,because) VALUES(new.id,new.frame_k,new.name,new.value,new.because); END;
    CREATE TRIGGER IF NOT EXISTS slot_ad AFTER DELETE ON slot BEGIN INSERT INTO slot_fts(slot_fts,rowid,frame_k,name,value,because) VALUES('delete',old.id,old.frame_k,old.name,old.value,old.because); END;
    CREATE TRIGGER IF NOT EXISTS slot_au AFTER UPDATE ON slot BEGIN
      INSERT INTO slot_fts(slot_fts,rowid,frame_k,name,value,because) VALUES('delete',old.id,old.frame_k,old.name,old.value,old.because);
      INSERT INTO slot_fts(rowid,frame_k,name,value,because) VALUES(new.id,new.frame_k,new.name,new.value,new.because);
    END;

    CREATE VIEW IF NOT EXISTS node_now AS SELECT * FROM node WHERE valid_to IS NULL;
    CREATE VIEW IF NOT EXISTS triple_now AS SELECT * FROM triple WHERE valid_to IS NULL;
    CREATE VIEW IF NOT EXISTS slot_now AS SELECT * FROM slot WHERE valid_to IS NULL;

    -- Populated from the ecosystem's own spine file (spine-import), never compiled in: which short
    -- name means which project is the one thing that cannot be true of every repo.
    CREATE TABLE IF NOT EXISTS proj_alias (short TEXT PRIMARY KEY, k TEXT NOT NULL);
    CREATE VIEW IF NOT EXISTS legacy_consumes AS
      SELECT COALESCE(a.k,'proj:'||e.project) AS s, 'consumes' AS p,
             'contract:'||e.contract||'.'||e.symbol AS o, e.file, e.line, e.usage, e.hardcoded
      FROM edges e LEFT JOIN proj_alias a ON a.short = e.project;

    CREATE TABLE IF NOT EXISTS distill_log (
      node_k TEXT NOT NULL, source_file TEXT NOT NULL, content_hash TEXT NOT NULL,
      dropped TEXT DEFAULT '', unresolved TEXT DEFAULT '', PRIMARY KEY (node_k, source_file)
    );
    CREATE INDEX IF NOT EXISTS ix_distill_hash ON distill_log(content_hash);

    -- Learning signal: every time a node is surfaced by a read and used, hits+last_used bump. Keyed by the
    -- stable node_k so it survives bi-temporal supersession. importance(hits)+recency feed recall ranking, so
    -- the brain gets sharper at what matters the more it's used. Decay is implicit: unused nodes lose recency.
    CREATE TABLE IF NOT EXISTS usage (node_k TEXT PRIMARY KEY, hits INTEGER NOT NULL DEFAULT 0, last_used TEXT, verified_at TEXT);
    """);
    TryExec("ALTER TABLE usage ADD COLUMN verified_at TEXT"); // migrate stores created before the freshness column
}

string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
string Pos1() => Positionals().Skip(1).FirstOrDefault() ?? "";
string? ReadFactJson(string k)
{
    using SqliteCommand c = db.CreateCommand();
    c.CommandText = "SELECT term,aliases,category,value,source,notes FROM facts WHERE k=$k";
    c.Parameters.AddWithValue("$k", k);
    using SqliteDataReader r = c.ExecuteReader();
    if (!r.Read()) return null;
    static string E(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
    return "{" +
        $"\"term\":{E(r.GetString(0))},\"aliases\":{E(r.GetString(1))},\"category\":{E(r.GetString(2))}," +
        $"\"value\":{E(r.GetString(3))},\"source\":{E(r.GetString(4))},\"notes\":{E(r.GetString(5))}}}";
}

// Upsert current state + keep FTS in sync + append to the cold mutation log. Caller owns the transaction.
void UpsertFact(string k, string term, string aliases, string category, string value, string source, string notes, string why)
{
    string? before = ReadFactJson(k);
    Run("INSERT INTO facts(k,term,aliases,category,value,source,notes) VALUES($k,$t,$al,$c,$v,$s,$n) " +
        "ON CONFLICT(k) DO UPDATE SET term=$t,aliases=$al,category=$c,value=$v,source=$s,notes=$n",
        ("$k", k), ("$t", term), ("$al", aliases), ("$c", category), ("$v", value), ("$s", source), ("$n", notes));
    Run("DELETE FROM facts_fts WHERE k=$k", ("$k", k));
    Run("INSERT INTO facts_fts(k,term,aliases,category,value,notes) VALUES($k,$t,$al,$c,$v,$n)",
        ("$k", k), ("$t", term), ("$al", aliases), ("$c", category), ("$v", value), ("$n", notes));
    string after = ReadFactJson(k)!;
    // The cold log records changes, not no-op rewrites — so re-imports/re-indexes never spam it.
    if (before != after)
        LogMutation("fact", k, before is null ? "insert" : "update", before, after, why);
}

// The single append-only write path: every knowledge-base mutation (fact/edge/todo/finding)
// lands here with its before/after snapshot, so the cold log is the complete trace. Caller owns the transaction.
void LogMutation(string kind, string key, string op, string? before, string? after, string why)
{
    Run("INSERT INTO mutations(ts,op,kind,k,before,after,why) VALUES($ts,$op,$kind,$k,$b,$af,$why)",
        ("$ts", Now()), ("$op", op), ("$kind", kind), ("$k", key), ("$b", before), ("$af", after), ("$why", why));
}

void CloseTodo(int id)
{
    // Guard: only an OPEN todo can be closed — never report success or log a phantom mutation for a no-op.
    string? title = ScalarStr("SELECT title FROM todos WHERE id=$i AND status='open'", ("$i", id));
    if (title is null) { Console.WriteLine($"no open todo #{id}."); return; }
    Exec("BEGIN");
    Run("UPDATE todos SET status='done' WHERE id=$i", ("$i", id));
    LogMutation("todo", title, "update", "open", "done", "");
    Exec("COMMIT");
    Console.WriteLine($"todo #{id} closed.");
}

string? ScalarStr(string sql, params (string name, object? val)[] ps)
{
    using SqliteCommand c = db.CreateCommand();
    c.CommandText = sql;
    foreach ((string name, object? val) in ps) c.Parameters.AddWithValue(name, val ?? DBNull.Value);
    object? o = c.ExecuteScalar();
    return o is string s ? s : o?.ToString();
}

long ScalarLong(string sql, params (string name, object? val)[] ps)
{
    using SqliteCommand c = db.CreateCommand();
    c.CommandText = sql;
    foreach ((string name, object? val) in ps) c.Parameters.AddWithValue(name, val ?? DBNull.Value);
    return (long)(c.ExecuteScalar() ?? 0L);
}

string? ScalarText(string sql, params (string name, object? val)[] ps)
{
    using SqliteCommand c = db.CreateCommand();
    c.CommandText = sql;
    foreach ((string name, object? val) in ps) c.Parameters.AddWithValue(name, val ?? DBNull.Value);
    object? result = c.ExecuteScalar();
    return result is null or DBNull ? null : (string)result;
}

// Split on every non-alphanumeric, not just space. Stripping the punctuation out of a whole word glued
// "nomercy-app-kmp" into "nomercyappkmp" and "the-effortless-encoder" into one nonsense token, neither
// of which exists in the index — FTS5's own tokenizer breaks on those same characters, so any query
// written the way a repo, package, or folder is actually named matched nothing at all.
List<string> Tokens(string terms) =>
    terms.ToLowerInvariant()
        .Split(" \t\r\n-_./\\,;:()[]{}<>\"'`|!?*+=&#@~%$^".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
        .Where(t => t.All(char.IsLetterOrDigit) && t.Length > 1 && !stop.Contains(t))
        .Distinct()
        .ToList();

string BuildMatch(string terms) => string.Join(" OR ", Tokens(terms).Select(t => $"\"{t}\""));

// FTS5 MATCH isolated in a subquery so it actually filters (a JOIN+MATCH leaked all rows).
// bm25 lower = better; the score is returned so callers can apply a relevance gate.
(List<(string term, string category, string value, string source, double score)> rows, double ms) Search(string terms, int limit)
{
    List<(string, string, string, string, double)> rows = new();
    string match = BuildMatch(terms);
    if (match.Length == 0) return (rows, 0);
    List<string> qToks = Tokens(terms);
    bool gate = ScalarLong("SELECT count(*) FROM facts") >= FloorMinCorpus;
    Stopwatch sw = Stopwatch.StartNew();
    using SqliteCommand c = db.CreateCommand();
    c.CommandText = @"SELECT f.term,f.category,f.value,f.source,m.score,f.aliases,f.notes
        FROM (SELECT k, bm25(facts_fts) AS score FROM facts_fts WHERE facts_fts MATCH $m ORDER BY score LIMIT $l) m
        JOIN facts f ON f.k=m.k ORDER BY m.score";
    c.Parameters.AddWithValue("$m", match);
    c.Parameters.AddWithValue("$l", limit);
    using SqliteDataReader r = c.ExecuteReader();
    while (r.Read())
    {
        double score = r.GetDouble(4);
        if (gate && score > RelevanceFloor) continue; // weak/low-IDF match in a populated store — refuse rather than surface noise
        // Coverage gate: a 3+ token question that matches on a single tangential word isn't an answer
        // (scale-invariant; catches "moooom design system" -> storage fact, "InfiniFrame ..." -> envelopes).
        string hay = $"{r.GetString(0)} {r.GetString(5)} {r.GetString(1)} {r.GetString(2)} {r.GetString(6)}".ToLowerInvariant();
        int present = qToks.Count(t => hay.Contains(t, StringComparison.Ordinal));
        if (qToks.Count >= 3 && present <= 1) continue;
        rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), score));
    }
    sw.Stop();
    return (rows, sw.Elapsed.TotalMilliseconds);
}

// A document's own name usually lives in its path, not its prose. The 12-part encoder report never
// writes the words "effortless encoder" in its body — the name is the folder — so a search for the
// thing by name returned a one-line stub instead of the 157 sections that answer the question.
// Folder and file names therefore become searchable text.
string PathTerms(string fullPath)
{
    string[] generic =
    {
        "src", "content", "site", "docs", "doc", "md", "readme", "index", "app", "apps", "packages",
        "projects", "c", "entries", "reports", "claude", "work", "public", "assets", "pages",
    };
    IEnumerable<string> words = Regex.Split(fullPath, @"[\\/\-_. ]+")
        .Select(w => w.Trim().ToLowerInvariant())
        .Where(w => w.Length > 1 && !w.All(char.IsDigit) && !generic.Contains(w));
    return string.Join(' ', words.Distinct());
}

// Store an answer that was distilled from a set of source files.
//
// The compacted sources are barely smaller than the originals — prose has no markdown chrome to strip —
// so serving them back saves nothing. What is worth keeping is the ANSWER: a session read twelve files
// totalling 140 KB to write a 15 KB orientation brief, threw the brief away, and left the next person to
// read the same 140 KB. A synthesis is that brief, filed against the directory it came from.
//
// It is explicitly second-hand: category 'synthesis', sources listed, and stamped with the newest source
// mtime so a reader can tell when the sources have moved on underneath it.

// Shed outdated absorbed docs by path substring (e.g. a superseded plan or a historical session log).
// Still called by SelfTest (shed-doc itself now dispatches through ShedDocTool).
void ShedDoc(string pathFragment)
{
    string like = "%" + pathFragment + "%";
    long before = ScalarLong("SELECT count(*) FROM docs WHERE path LIKE $p", ("$p", like));
    if (before == 0) { Console.WriteLine($"no docs match path '{pathFragment}'."); return; }
    Exec("BEGIN");
    Run("DELETE FROM docs_fts WHERE k IN (SELECT k FROM docs WHERE path LIKE $p)", ("$p", like));
    Run("DELETE FROM docs WHERE path LIKE $p", ("$p", like));
    Exec("COMMIT");
    // The read gate keeps its own index of which directories have a synthesis. Leaving a shed entry in
    // it means the gate keeps claiming an answer exists for a row that is gone.
    ForgetSynthesisIndex(pathFragment);
    Console.WriteLine($"shed {before} section(s) matching '{pathFragment}'.");
}

// Drop matching directories from the read gate's synthesis index, which lives beside the store as JSON
// because the gate runs on every Read and cannot afford to open the database to find out there is nothing.
// Still called (via ShedDoc) by SelfTest.
void ForgetSynthesisIndex(string pathFragment)
{
    string idx = Path.Combine(root, "synthesis.json");
    if (!File.Exists(idx)) return;
    try
    {
        // JsonDocument, not JsonSerializer: reflection-based serialization is disabled in this build, so
        // Deserialize<T> compiles with a trim warning and then fails at runtime.
        string needle = pathFragment.Replace('\\', '/').ToLowerInvariant();
        List<(string key, string json)> keep = new();
        bool dropped = false;
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(idx)))
        {
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) { dropped = true; continue; }
                keep.Add((p.Name, p.Value.GetRawText()));
            }
        }
        if (!dropped) return;

        using FileStream fs = File.Create(idx);
        using Utf8JsonWriter w = new(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        foreach ((string k, string raw) in keep)
        {
            w.WritePropertyName(k);
            using JsonDocument v = JsonDocument.Parse(raw);
            v.RootElement.WriteTo(w);
        }
        w.WriteEndObject();
    }
    catch { /* the index is a cache; a malformed one degrades to "no synthesis known" */ }
}

// Forget one fact by key + keep FTS in sync + log the deletion to the cold trail.
// Mirrors ShedMemory. Manually-added facts (add --term) have no graph node, but a fact
// that WAS graph-linked would leave a dangling ref behind, so drop those too — the ref
// integrity trigger only guards inserts, never deletes. The key is the fact's term.
void ShedFact(string key)
{
    string? before = ReadFactJson(key);
    if (before is null) { Console.WriteLine($"no fact '{key}'."); return; }
    Exec("BEGIN");
    Run("DELETE FROM facts_fts WHERE k=$k", ("$k", key));
    Run("DELETE FROM facts WHERE k=$k", ("$k", key));
    Run("DELETE FROM ref WHERE channel='facts' AND payload_k=$k", ("$k", key));
    LogMutation("fact", key, "delete", before, null, "shed-fact");
    Exec("COMMIT");
    Console.WriteLine($"shed fact '{key}'.");
}

// Upsert a memory + keep FTS in sync + log to the cold trail (changes only, never no-op rewrites).
// Still called by SelfTest (index-memory itself now dispatches through IndexMemoryTool).
void UpsertMemory(string k, string type, string title, string hook, string body, string links, bool hard, string why)
{
    string? before = ScalarText("SELECT type||'|'||title||'|'||hook||'|'||body FROM memory WHERE k=$k", ("$k", k));
    Run("INSERT INTO memory(k,type,title,hook,body,links,hard) VALUES($k,$ty,$ti,$h,$b,$l,$hd) " +
        "ON CONFLICT(k) DO UPDATE SET type=$ty,title=$ti,hook=$h,body=$b,links=$l,hard=$hd",
        ("$k", k), ("$ty", type), ("$ti", title), ("$h", hook), ("$b", body), ("$l", links), ("$hd", hard ? 1 : 0));
    Run("DELETE FROM memory_fts WHERE k=$k", ("$k", k));
    Run("INSERT INTO memory_fts(k,title,hook,body) VALUES($k,$ti,$h,$b)", ("$k", k), ("$ti", title), ("$h", hook), ("$b", body));
    string after = $"{type}|{title}|{hook}|{body}";
    if (before != after) LogMutation("memory", k, before is null ? "insert" : "update", before, after, why);
}

// BRAIN router — one-shot recall over the knowledge graph. Each read is a single self-contained statement
// against the *_now (live-only) views; no nested calls, no query-time code grep.
void BrainCmd(List<string> rest)
{
    string sub = rest.Count > 0 ? rest[0] : "help";
    List<string> rargs = rest.Skip(1).ToList();
    switch (sub)
    {
        case "core": Console.WriteLine(new BrainCoreTool().ExecuteCli(db)); break;
        case "scope": Console.WriteLine(new BrainScopeTool().ExecuteCli(db, rargs)); break;
        case "common": Console.WriteLine(new BrainCommonTool().ExecuteCli(db, rargs)); break;
        case "place": Console.WriteLine(new BrainPlaceTool().ExecuteCli(db, rargs.FirstOrDefault() ?? "")); break;
        case "recall": BrainRecall(string.Join(' ', rargs)); break;
        case "impact": BrainImpact(string.Join(' ', rargs)); break;
        case "learn":
        {
            string learnResult = new BrainLearnTool().ExecuteCli(db, rargs, GetFlag("--gloss") ?? "",
                GetFlag("--scheme") ?? "", "text", GetFlag("--because") ?? "", a.Contains("--hard"), a.Contains("--multi"));
            if (learnResult.Length > 0) Console.WriteLine(learnResult);
            break;
        }
        case "learn-batch":
            Console.WriteLine(new BrainLearnBatchTool().ExecuteCli(db,
                GetFlag("--from") ?? throw new ArgumentException("brain learn-batch needs --from <file>")));
            break;
        case "set-hard":
            if (rargs.Count < 2) { Console.WriteLine("usage: brain set-hard <node-key> <0|1>"); break; }
            Console.WriteLine(new BrainSetHardTool().Execute(db, rargs[0], rargs[1] == "1"));
            break;
        case "export": Console.WriteLine(new BrainExportTool().ExecuteCli(db, GetFlag("--to") ?? Path.Combine(root, "brain-export.txt"))); break;
        case "audit": Console.WriteLine(new BrainAuditTool().Execute(db, instance)); break;
        case "tidy": Console.WriteLine(new BrainTidyTool().Execute(db, root)); break;
        case "why":
            Console.WriteLine(rargs.Count < 1 ? "usage: brain why <node-key>" : new BrainWhyTool().Execute(db, rargs[0]));
            break;
        case "merge":
            if (rargs.Count < 2) { Console.WriteLine("usage: brain merge <from-key> <into-key>"); break; }
            Console.WriteLine(new BrainMergeTool().Execute(db, root, rargs[0], rargs[1]));
            break;
        case "verify":
            Console.WriteLine(rargs.Count < 1 ? "usage: brain verify <node-key>" : new BrainVerifyTool().Execute(db, rargs[0]));
            break;
        case "forget":
            if (rargs.Count < 1) { Console.WriteLine("usage: brain forget <node-key>"); break; }
            Console.WriteLine(new BrainForgetTool().Execute(db, root, rargs[0]));
            break;
        case "unlink":
            if (rargs.Count < 3) { Console.WriteLine("usage: brain unlink <subject> <predicate> <object>"); break; }
            Console.WriteLine(new BrainUnlinkTool().Execute(db, root, rargs[0], rargs[1], rargs[2]));
            break;
        case "stale": Console.WriteLine(new BrainStaleTool().Execute(db, int.TryParse(GetFlag("--days"), out int sd) ? sd : 30)); break;
        case "distill": Console.WriteLine(new BrainDistillTool().Execute(db, root)); break;
        case "seed": BrainSeed(); break;
        case "stats": BrainStats(); break;
        case "gaps": Console.WriteLine(new BrainGapsTool().ExecuteCli(db)); break;
        default:
            Console.WriteLine("brain <core|scope <proj…>|common <proj…>|place <codekind>|recall <text>|impact <symbol>|learn …|gaps|distill|stats>");
            break;
    }
}

// Execute, print, and return each row's first column (the node key for a brain read). The reader is disposed
// before the caller reinforces, so the follow-up write never races an open reader on the single connection.
List<string> RunReader(SqliteCommand c, bool announceEmpty = true)
{
    List<string> keys = new();
    int n = 0;
    using (SqliteDataReader r = c.ExecuteReader())
    {
        while (r.Read())
        {
            List<string> cols = new();
            for (int i = 0; i < r.FieldCount; i++)
                cols.Add(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "");
            if (cols.Count > 0) keys.Add(cols[0]);
            Console.WriteLine("  " + string.Join("  |  ", cols));
            n++;
        }
        if (n == 0 && announceEmpty) Console.WriteLine("  (nothing)");
    }
    return keys;
}

// Reinforce surfaced nodes: bump hits + recency. Guarded on live-node existence so non-node first columns
// (slot names, project shorthands) never create junk usage rows. This is the automatic half of "smarter with use".
void Reinforce(IEnumerable<string> keys)
{
    foreach (string k in keys.Where(s => s.Length > 0).Distinct())
        Run("INSERT INTO usage(node_k,hits,last_used) SELECT $k,1,$ts WHERE EXISTS(SELECT 1 FROM node_now WHERE k=$k) " +
            "ON CONFLICT(node_k) DO UPDATE SET hits=hits+1, last_used=$ts",
            ("$k", k), ("$ts", Now()));
}

// Free-text recall — DB-side alias expansion -> porter FTS5 seed (live-filtered) -> same-statement traversal
// to who-uses-it + ref-pulled fact/rule. One natural-language question, one query.
void BrainRecall(string text)
{
    if (text.Trim().Length == 0) { Console.WriteLine("usage: brain recall <free text>"); return; }
    List<string> toks = Tokens(text);
    if (toks.Count == 0) { Console.WriteLine("  (no usable terms)"); return; }
    string rawList = string.Join(",", toks.Select((_, i) => $"($q{i})"));
    using SqliteCommand c = db.CreateCommand();
    c.CommandText = $@"WITH q(raw) AS (VALUES {rawList}),
        expanded AS (
          SELECT raw AS term FROM q
          UNION SELECT a.canonical FROM q JOIN term_alias a ON a.term = q.raw),
        match_expr AS (SELECT group_concat('""'||term||'""',' OR ') AS m FROM expanded),
        seed AS (
          SELECT n.id, n.k,
            bm25(node_fts)
              - 0.15 * MIN(COALESCE(u.hits, 0), 20)
              - CASE WHEN u.last_used IS NULL THEN 0
                     WHEN julianday('now') - julianday(u.last_used) < 1 THEN 0.6
                     WHEN julianday('now') - julianday(u.last_used) < 7 THEN 0.3
                     WHEN julianday('now') - julianday(u.last_used) < 30 THEN 0.1
                     ELSE 0 END AS r
          FROM node_fts f
          JOIN node_now n ON n.id = f.rowid
          LEFT JOIN usage u ON u.node_k = n.k
          WHERE node_fts MATCH (SELECT m FROM match_expr)
          ORDER BY r LIMIT 15)
        SELECT n.k, n.kind, n.label, n.gloss,
               (SELECT group_concat(DISTINCT t.s) FROM triple_now t
                WHERE t.o = n.k AND t.p IN ('consumes','governed_by','exposes','implements')) AS used_by,
               (SELECT m.body  FROM ref r JOIN memory m ON m.k = r.payload_k
                WHERE r.node_k = n.k AND r.channel='memory' LIMIT 1) AS rule,
               (SELECT fa.value FROM ref r JOIN facts fa ON fa.k = r.payload_k
                WHERE r.node_k = n.k AND r.channel='facts' LIMIT 1) AS fact
        FROM seed JOIN node_now n ON n.id = seed.id
        ORDER BY seed.r";
    for (int i = 0; i < toks.Count; i++) c.Parameters.AddWithValue($"$q{i}", toks[i]);
    List<string> hits = RunReader(c, announceEmpty: false);
    Reinforce(hits);
    if (hits.Count == 0)
    {
        // FTS + synonyms missed — substring fallback over label/gloss so a real query rarely comes up empty.
        using SqliteCommand fb = db.CreateCommand();
        string likeClauses = string.Join(" OR ", toks.Select((_, i) => $"label LIKE $l{i} OR gloss LIKE $l{i}"));
        fb.CommandText = $"SELECT k, kind, label, gloss FROM node_now WHERE {likeClauses} LIMIT 8";
        for (int i = 0; i < toks.Count; i++) fb.Parameters.AddWithValue($"$l{i}", "%" + toks[i] + "%");
        Console.WriteLine("  (no FTS match — substring fallback)");
        List<string> fbHits = RunReader(fb);
        Reinforce(fbHits);
        if (fbHits.Count == 0) LogGap("brain_recall", text);
    }
}

// Cross-project impact — every consumer of a shared symbol/contract from the still-live, alias-corrected
// edges view plus the summary triple. Call before changing a shared contract.
void BrainImpact(string term)
{
    if (term.Trim().Length == 0) { Console.WriteLine("usage: brain impact <symbol-or-contract>"); return; }
    using SqliteCommand c = db.CreateCommand();
    c.CommandText = @"SELECT s AS project, o AS contract_symbol, file, line, hardcoded
        FROM legacy_consumes WHERE o LIKE $like
        UNION ALL
        SELECT substr(t.s,6), t.o, NULL, NULL, NULL
        FROM triple_now t
        WHERE t.p='consumes' AND t.o LIKE $like AND t.o_is_literal=0";
    c.Parameters.AddWithValue("$like", "%" + term + "%");
    Reinforce(RunReader(c));
}

void BrainStats()
{
    Console.WriteLine($"  nodes   {ScalarLong("SELECT count(*) FROM node_now")}  ({ScalarLong("SELECT count(*) FROM node_now WHERE hard=1")} hard)");
    Console.WriteLine($"  triples {ScalarLong("SELECT count(*) FROM triple_now")}");
    Console.WriteLine($"  slots   {ScalarLong("SELECT count(*) FROM slot_now")}");
    Console.WriteLine($"  refs    {ScalarLong("SELECT count(*) FROM ref")}");
    Console.WriteLine($"  usage   {ScalarLong("SELECT count(*) FROM usage")} reinforced; top: {ScalarText("SELECT group_concat(node_k||'×'||hits,', ') FROM (SELECT node_k,hits FROM usage ORDER BY hits DESC LIMIT 3)") ?? "(none yet)"}");
    Console.WriteLine($"  coverage  seams {ScalarLong("SELECT count(*) FROM node_now n WHERE kind='seam' AND EXISTS(SELECT 1 FROM triple_now WHERE o=n.k AND p='consumes')")}/{ScalarLong("SELECT count(*) FROM node_now WHERE kind='seam'")} consumed, codekinds {ScalarLong("SELECT count(*) FROM node_now n WHERE kind='codekind' AND EXISTS(SELECT 1 FROM slot_now WHERE frame_k=n.k)")}/{ScalarLong("SELECT count(*) FROM node_now WHERE kind='codekind'")} placed");
    Console.WriteLine($"  fresh   {ScalarLong("SELECT count(*) FROM node_now n JOIN usage u ON u.node_k=n.k WHERE u.verified_at IS NOT NULL")}/{ScalarLong("SELECT count(*) FROM node_now")} confirmed against code");
    Console.WriteLine($"  gaps    {ScalarLong("SELECT count(*) FROM gaps WHERE status='open'")} open / {ScalarLong("SELECT count(*) FROM gaps")} total (unanswerable lookups awaiting a learn)");
    using SqliteCommand c = db.CreateCommand();
    c.CommandText = "SELECT kind, count(*) FROM node_now GROUP BY kind ORDER BY 2 DESC";
    using SqliteDataReader r = c.ExecuteReader();
    while (r.Read()) Console.WriteLine($"    {r.GetString(0),-12} {r.GetInt32(1)}");
}

// --- Write primitives (the write-last path). Caller owns the transaction. Supersede, never delete; the
//     node_*/slot_* triggers keep FTS synced and the triple triggers reject typo'd predicate/dangling refs. ---

// Upsert a node: noop if unchanged, else supersede the live row (3-step: close old, insert new, back-fill
// superseded_by — the id is autoincrement so the new id is only known after insert).
void AddNode(string k, string kind, string label, string gloss, string scheme, bool hard, string why)
{
    string sig = $"{kind}|{label}|{gloss}|{scheme}|{(hard ? 1 : 0)}";
    string? before = ScalarText("SELECT kind||'|'||label||'|'||gloss||'|'||COALESCE(scheme,'')||'|'||hard FROM node_now WHERE k=$k", ("$k", k));
    if (before == sig) return;
    if (before != null) Run("UPDATE node SET valid_to=$now WHERE k=$k AND valid_to IS NULL", ("$now", Now()), ("$k", k));
    Run("INSERT INTO node(k,kind,label,gloss,scheme,hard) VALUES($k,$ki,$l,$g,$s,$h)",
        ("$k", k), ("$ki", kind), ("$l", label), ("$g", gloss), ("$s", scheme.Length == 0 ? null : scheme), ("$h", hard ? 1 : 0));
    if (before != null)
    {
        long newId = ScalarLong("SELECT id FROM node_now WHERE k=$k", ("$k", k));
        Run("UPDATE node SET superseded_by=$nid WHERE k=$k AND valid_to IS NOT NULL AND superseded_by IS NULL", ("$nid", newId), ("$k", k));
    }
    LogMutation("node", k, before is null ? "insert" : "update", before, sig, why);
}

// Insert a triple idempotently. o_is_literal is DERIVED from pred_vocab.is_link (never trusted from a caller);
// the triggers enforce vocab + that the subject (and link object) reference a live node.
void AddTriple(string s, string p, string o, string because, string src, bool hard, string why)
{
    if (ScalarLong("SELECT count(*) FROM pred_vocab WHERE p=$p", ("$p", p)) == 0)
    {
        Console.Error.WriteLine($"unknown predicate '{p}' — add it to pred_vocab first.");
        return;
    }
    if (ScalarLong("SELECT count(*) FROM triple_now WHERE s=$s AND p=$p AND o=$o", ("$s", s), ("$p", p), ("$o", o)) > 0)
    {
        // re-asserting a known relation is confirmation, not a no-op: strengthen it (conf feeds scope weighting).
        Run("UPDATE triple SET conf=MIN(conf+0.2,3.0) WHERE s=$s AND p=$p AND o=$o AND valid_to IS NULL", ("$s", s), ("$p", p), ("$o", o));
        return;
    }
    // bidirectional: warn whether the existing edge declares THIS predicate as its conflict, or this predicate
    // declares the existing one — conflicts in pred_vocab are not always mutual, so check both directions.
    if (ScalarLong(@"SELECT count(*) FROM triple_now t JOIN pred_vocab v ON v.p=t.p
            WHERE t.s=$s AND t.o=$o AND (v.conflicts=$p OR t.p=(SELECT conflicts FROM pred_vocab WHERE p=$p))",
            ("$s", s), ("$o", o), ("$p", p)) > 0)
        Console.Error.WriteLine($"warning: '{s} {p} {o}' contradicts an existing edge on the same pair — review.");
    long isLink = ScalarLong("SELECT is_link FROM pred_vocab WHERE p=$p", ("$p", p));
    Run("INSERT INTO triple(s,p,o,o_is_literal,because,src,hard) VALUES($s,$p,$o,$lit,$b,$src,$h)",
        ("$s", s), ("$p", p), ("$o", o), ("$lit", 1 - isLink), ("$b", because), ("$src", src), ("$h", hard ? 1 : 0));
    LogMutation("triple", $"{s} {p} {o}", "insert", null, $"{s}|{p}|{o}", why);
}

// Upsert a slot. multi=0 single-valued (supersede on change, no contradiction); multi=1 set-valued (add).
void AddSlot(string frame, string name, string value, string facet, bool multi, string because, string src, string why)
{
    if (multi)
    {
        if (ScalarLong("SELECT count(*) FROM slot_now WHERE frame_k=$f AND name=$n AND value=$v", ("$f", frame), ("$n", name), ("$v", value)) > 0) return;
        Run("INSERT INTO slot(frame_k,name,value,facet,multi,because,src) VALUES($f,$n,$v,$fa,1,$b,$src)",
            ("$f", frame), ("$n", name), ("$v", value), ("$fa", facet), ("$b", because), ("$src", src));
        LogMutation("slot", $"{frame}/{name}={value}", "insert", null, value, why);
        return;
    }
    string? before = ScalarText("SELECT value FROM slot_now WHERE frame_k=$f AND name=$n AND multi=0", ("$f", frame), ("$n", name));
    if (before == value) return;
    if (before != null) Run("UPDATE slot SET valid_to=$now WHERE frame_k=$f AND name=$n AND multi=0 AND valid_to IS NULL", ("$now", Now()), ("$f", frame), ("$n", name));
    Run("INSERT INTO slot(frame_k,name,value,facet,multi,because,src) VALUES($f,$n,$v,$fa,0,$b,$src)",
        ("$f", frame), ("$n", name), ("$v", value), ("$fa", facet), ("$b", because), ("$src", src));
    if (before != null)
    {
        long nid = ScalarLong("SELECT id FROM slot_now WHERE frame_k=$f AND name=$n AND multi=0", ("$f", frame), ("$n", name));
        Run("UPDATE slot SET superseded_by=$nid WHERE frame_k=$f AND name=$n AND valid_to IS NOT NULL AND superseded_by IS NULL", ("$nid", nid), ("$f", frame), ("$n", name));
    }
    LogMutation("slot", $"{frame}/{name}", before is null ? "insert" : "update", before, value, why);
}

void BrainLearn(List<string> args)
{
    string what = args.Count > 0 ? args[0] : "";
    List<string> p = args.Skip(1).ToList();
    Exec("BEGIN");
    switch (what)
    {
        case "node":
            if (p.Count < 3) { Console.WriteLine("usage: brain learn node <k> <kind> <label> [--gloss ..] [--scheme ..] [--hard]"); break; }
            string learnGuard = NodeGuard(p[1], string.Join(' ', p.Skip(2)));
            if (learnGuard.Length > 0) { Console.WriteLine(learnGuard); break; }
            AddNode(p[0], p[1], string.Join(' ', p.Skip(2)), GetFlag("--gloss") ?? "", GetFlag("--scheme") ?? "", a.Contains("--hard"), "brain learn");
            break;
        case "triple":
            if (p.Count < 3) { Console.WriteLine("usage: brain learn triple <s> <predicate> <o> [--because ..] [--hard]"); break; }
            AddTriple(p[0], p[1], p[2], GetFlag("--because") ?? "", "manual", a.Contains("--hard"), "brain learn");
            break;
        case "slot":
            if (p.Count < 3) { Console.WriteLine("usage: brain learn slot <frame> <name> <value> [--facet ..] [--multi] [--because ..]"); break; }
            AddSlot(p[0], p[1], string.Join(' ', p.Skip(2)), GetFlag("--facet") ?? "text", a.Contains("--multi"), GetFlag("--because") ?? "", "manual", "brain learn");
            break;
        default:
            Console.WriteLine("brain learn <node|triple|slot> …");
            break;
    }
    Exec("COMMIT");
    if (what is "node" or "triple" or "slot" && p.Count >= 3)
        ResolveGaps($"{string.Join(' ', p)} {GetFlag("--gloss") ?? ""} {GetFlag("--because") ?? ""}");
}

// Deliberate removal: retire a node confirmed wrong/obsolete along with its live edges and slots, so the audit
// stays clean (no dead-reference triples left behind). The "this is wrong, kill it" half of the freshness loop.
void BrainForget(string k)
{
    if (ScalarLong("SELECT count(*) FROM node_now WHERE k=$k", ("$k", k)) == 0) { Console.WriteLine($"no live node '{k}'."); return; }
    string now = Now();
    Exec("BEGIN");
    Run("UPDATE triple SET valid_to=$now WHERE valid_to IS NULL AND (s=$k OR (o=$k AND o_is_literal=0))", ("$now", now), ("$k", k));
    Run("UPDATE slot SET valid_to=$now WHERE valid_to IS NULL AND frame_k=$k", ("$now", now), ("$k", k));
    Run("DELETE FROM ref WHERE node_k=$k", ("$k", k));
    Run("UPDATE node SET valid_to=$now WHERE k=$k AND valid_to IS NULL", ("$now", now), ("$k", k));
    LogMutation("node", k, "forget", k, null, "retired");
    Exec("COMMIT");
    Console.WriteLine($"forgot {k} (node + its live edges/slots retired).");
}

// Freshness write-path: mark a node re-confirmed against current reality (the half of the staleness loop that
// brain stale surfaces). Confirmation is not the same as being surfaced, so it has its own timestamp.
void BrainVerify(string k)
{
    if (ScalarLong("SELECT count(*) FROM node_now WHERE k=$k", ("$k", k)) == 0) { Console.WriteLine($"no live node '{k}'."); return; }
    Run("INSERT INTO usage(node_k,hits,last_used,verified_at) VALUES($k,0,$ts,$ts) ON CONFLICT(node_k) DO UPDATE SET verified_at=$ts", ("$k", k), ("$ts", Now()));
    Console.WriteLine($"verified {k} (confirmed against current code).");
}

// Dedup: fold a duplicate node into the canonical one. Re-points triples (subject + link object), slots, refs
// and usage with collision-safe guards (a move that would duplicate a live edge/slot is dropped instead),
// sums usage counts, then retires the source via supersession. FTS stays correct because reads join node_now.
void BrainMerge(string from, string to)
{
    if (from == to) { Console.WriteLine("nothing to merge (from == into)."); return; }
    if (ScalarLong("SELECT count(*) FROM node_now WHERE k=$k", ("$k", to)) == 0) { Console.WriteLine($"target '{to}' is not a live node."); return; }
    if (ScalarLong("SELECT count(*) FROM node_now WHERE k=$k", ("$k", from)) == 0) { Console.WriteLine($"source '{from}' is not a live node."); return; }
    string now = Now();
    Exec("BEGIN");
    // triple subjects: move those that won't collide on (s,p,o), then close the rest
    Run("UPDATE triple SET s=$to WHERE s=$from AND valid_to IS NULL AND NOT EXISTS (SELECT 1 FROM triple t2 WHERE t2.valid_to IS NULL AND t2.s=$to AND t2.p=triple.p AND t2.o=triple.o)", ("$to", to), ("$from", from));
    Run("UPDATE triple SET valid_to=$now WHERE s=$from AND valid_to IS NULL", ("$now", now), ("$from", from));
    // triple link-objects: same guard
    Run("UPDATE triple SET o=$to WHERE o=$from AND o_is_literal=0 AND valid_to IS NULL AND NOT EXISTS (SELECT 1 FROM triple t2 WHERE t2.valid_to IS NULL AND t2.s=triple.s AND t2.p=triple.p AND t2.o=$to)", ("$to", to), ("$from", from));
    Run("UPDATE triple SET valid_to=$now WHERE o=$from AND o_is_literal=0 AND valid_to IS NULL", ("$now", now), ("$from", from));
    // slots: move only when the target has no slot of that name (avoids the partial unique indexes), close the rest
    Run("UPDATE slot SET frame_k=$to WHERE frame_k=$from AND valid_to IS NULL AND NOT EXISTS (SELECT 1 FROM slot s2 WHERE s2.valid_to IS NULL AND s2.frame_k=$to AND s2.name=slot.name)", ("$to", to), ("$from", from));
    Run("UPDATE slot SET valid_to=$now WHERE frame_k=$from AND valid_to IS NULL", ("$now", now), ("$from", from));
    // refs: move non-colliding, drop the rest
    Run("UPDATE OR IGNORE ref SET node_k=$to WHERE node_k=$from", ("$to", to), ("$from", from));
    Run("DELETE FROM ref WHERE node_k=$from", ("$from", from));
    // usage: fold the source's hits into the target, then drop the source row
    Run("UPDATE usage SET hits=hits+COALESCE((SELECT hits FROM usage WHERE node_k=$from),0) WHERE node_k=$to", ("$to", to), ("$from", from));
    Run("UPDATE OR IGNORE usage SET node_k=$to WHERE node_k=$from", ("$to", to), ("$from", from));
    Run("DELETE FROM usage WHERE node_k=$from", ("$from", from));
    // retire the source node
    Run("UPDATE node SET valid_to=$now WHERE k=$from AND valid_to IS NULL", ("$now", now), ("$from", from));
    LogMutation("node", from, "merge", from, to, $"merged into {to}");
    Exec("COMMIT");
    Console.WriteLine($"merged {from} -> {to}.");
}

// Write-side brake. "Get smarter over time" needs NEW knowledge to actually land, and brain_learn is
// unenforced — so a durable fact noticed mid-turn rots the same way MEMORY.md did. `stage` resolves a learn
// the instant it's noticed (cheap append, no DB write); `flush` commits the batch; the Stop hook
// (loop-guard.mjs) refuses to end the turn while pending-learn.jsonl is non-empty. Staging is still my
// judgement — the brake only guarantees that nothing STAGED is ever silently dropped before it's persisted.
void StageCmd(List<string> pos)
{
    string J(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ") + "\"";
    string sub = pos.Count > 0 ? pos[0] : "list";
    string ledger = Path.Combine(root, "pending-learn.jsonl");
    switch (sub)
    {
        case "node":
            if (pos.Count < 4) { Console.WriteLine("usage: stage node <k> <kind> <label> [--gloss ..] [--scheme ..] [--hard]"); break; }
            string stageGuard = NodeGuard(pos[2], string.Join(' ', pos.Skip(3)));
            if (stageGuard.Length > 0) { Console.WriteLine(stageGuard); break; }
            File.AppendAllText(ledger, "{" + $"\"k\":\"node\",\"key\":{J(pos[1])},\"kind\":{J(pos[2])},\"label\":{J(string.Join(' ', pos.Skip(3)))},\"gloss\":{J(GetFlag("--gloss") ?? "")},\"scheme\":{J(GetFlag("--scheme") ?? "")},\"hard\":{(a.Contains("--hard") ? "true" : "false")}" + "}\n");
            Console.WriteLine($"staged node {pos[1]} (owe flush).");
            break;
        case "triple":
            if (pos.Count < 4) { Console.WriteLine("usage: stage triple <s> <predicate> <o> [--because ..] [--hard]"); break; }
            File.AppendAllText(ledger, "{" + $"\"k\":\"triple\",\"s\":{J(pos[1])},\"p\":{J(pos[2])},\"o\":{J(pos[3])},\"because\":{J(GetFlag("--because") ?? "")},\"hard\":{(a.Contains("--hard") ? "true" : "false")}" + "}\n");
            Console.WriteLine($"staged triple {pos[1]} {pos[2]} {pos[3]} (owe flush).");
            break;
        case "slot":
            if (pos.Count < 4) { Console.WriteLine("usage: stage slot <frame> <name> <value> [--facet ..] [--multi] [--because ..]"); break; }
            File.AppendAllText(ledger, "{" + $"\"k\":\"slot\",\"frame\":{J(pos[1])},\"name\":{J(pos[2])},\"value\":{J(string.Join(' ', pos.Skip(3)))},\"facet\":{J(GetFlag("--facet") ?? "text")},\"multi\":{(a.Contains("--multi") ? "true" : "false")},\"because\":{J(GetFlag("--because") ?? "")}" + "}\n");
            Console.WriteLine($"staged slot {pos[1]}.{pos[2]} (owe flush).");
            break;
        case "list":
            Console.WriteLine(File.Exists(ledger) && File.ReadAllText(ledger).Trim().Length > 0 ? File.ReadAllText(ledger).TrimEnd() : "nothing staged.");
            break;
        case "clear":
        case "dismiss":
            if (File.Exists(ledger)) File.Delete(ledger);
            Console.WriteLine("staged learnings cleared (nothing persisted).");
            break;
        default:
            Console.WriteLine("stage <node|triple|slot|list|clear> …");
            break;
    }
}

// Commit every staged learning in one transaction, then delete the ledger so the Stop-hook brake releases.
// Re-uses the same Add* writers as brain learn, so supersession / conf-reinforcement / dangling-ref rejection
// all apply identically — flush is just the deferred, batched form of brain learn.
void FlushCmd()
{
    static string JStr(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static bool JBool(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
    string ledger = Path.Combine(root, "pending-learn.jsonl");
    if (!File.Exists(ledger)) { Console.WriteLine("nothing staged."); return; }
    string[] lines = File.ReadAllLines(ledger).Where(l => l.Trim().Length > 0).ToArray();
    if (lines.Length == 0) { File.Delete(ledger); Console.WriteLine("nothing staged."); return; }
    int n = 0;
    Exec("BEGIN");
    foreach (string line in lines)
    {
        using JsonDocument d = JsonDocument.Parse(line);
        JsonElement e = d.RootElement;
        switch (JStr(e, "k"))
        {
            case "node": AddNode(JStr(e, "key"), JStr(e, "kind"), JStr(e, "label"), JStr(e, "gloss"), JStr(e, "scheme"), JBool(e, "hard"), "flush"); n++; break;
            case "triple": AddTriple(JStr(e, "s"), JStr(e, "p"), JStr(e, "o"), JStr(e, "because"), "manual", JBool(e, "hard"), "flush"); n++; break;
            case "slot": AddSlot(JStr(e, "frame"), JStr(e, "name"), JStr(e, "value"), JStr(e, "facet"), JBool(e, "multi"), JStr(e, "because"), "manual", "flush"); n++; break;
        }
    }
    Exec("COMMIT");
    File.Delete(ledger);
    ResolveGaps(string.Join(' ', lines));
    Console.WriteLine($"flushed {n} learning(s) into the brain.");
}

// PHASE B — the curated spine: the ecosystem as its operator holds it in their head (projects, platforms, shared
// seams, contracts, placement frames). This is irreducible curation, not a script — it is the knowledge that
// makes scope/common/place answer in one shot. Run AFTER `brain distill` so governed_by can target rule nodes.
void BrainSeed()
{
    // The curated spine is one ecosystem's knowledge, not engine behaviour, so it is data rather than
    // code: 145 lines of one company's projects, seams and contracts used to be compiled into every
    // copy of this binary. Export it once with spine-export, keep it beside the store, load it here.
    string seed = GetFlag("--from") ?? Path.Combine(AppContext.BaseDirectory, "..", "seeds", "spine.json");
    if (!File.Exists(seed))
    {
        Console.Error.WriteLine($"no spine file at {Path.GetFullPath(seed)} — run `aitm spine-export` on an instance that already has one, or write the file by hand (see README).");
        return;
    }
    SpineImport(seed);
}

// The curated spine is per-ecosystem knowledge, not engine code, so it lives in a JSON file next to
// the store rather than compiled into the binary. Export reads whatever is currently seeded; import
// replays it. Together they let one ecosystem's spine be versioned, shared, or swapped out entirely
// without touching this program.
void SpineExport(string toPath)
{
    List<Dictionary<string, object?>> Rows(string sql)
    {
        List<Dictionary<string, object?>> rows = new();
        using SqliteCommand c = db.CreateCommand();
        c.CommandText = sql;
        using SqliteDataReader r = c.ExecuteReader();
        while (r.Read())
        {
            Dictionary<string, object?> row = new();
            for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    (string name, List<Dictionary<string, object?>> rows)[] sections =
    {
        ("nodes", Rows("SELECT k,kind,label,gloss,COALESCE(scheme,'') AS scheme,hard FROM node WHERE valid_to IS NULL ORDER BY k")),
        ("slots", Rows("SELECT frame_k,name,value,COALESCE(facet,'text') AS facet,multi FROM slot WHERE valid_to IS NULL ORDER BY frame_k,name")),
        ("links", Rows("SELECT s,p,o,COALESCE(because,'') AS because FROM triple WHERE valid_to IS NULL AND o_is_literal=0 AND src='seed' ORDER BY s,p,o")),
        ("aliases", Rows("SELECT short,k FROM proj_alias ORDER BY short")),
        ("terms", Rows("SELECT term,canonical FROM term_alias ORDER BY term,canonical")),
        ("edges", Rows("SELECT symbol,COALESCE(contract,'') AS contract,COALESCE(project,'') AS project,file,line,COALESCE(usage,'') AS usage,COALESCE(hardcoded,0) AS hardcoded FROM edges WHERE contract <> 'decl' ORDER BY symbol,file")),
    };

    // Written field by field: this build disables reflection-based serialization, and the shape is
    // small and fixed anyway.
    string full = Path.GetFullPath(toPath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    using (FileStream fs = File.Create(full))
    using (Utf8JsonWriter w = new(fs, new JsonWriterOptions { Indented = true }))
    {
        w.WriteStartObject();
        foreach ((string name, List<Dictionary<string, object?>> rows) in sections)
        {
            w.WriteStartArray(name);
            foreach (Dictionary<string, object?> row in rows)
            {
                w.WriteStartObject();
                foreach (KeyValuePair<string, object?> cell in row)
                {
                    if (cell.Value is long l) w.WriteNumber(cell.Key, l);
                    else if (cell.Value is null) w.WriteNull(cell.Key);
                    else w.WriteString(cell.Key, Convert.ToString(cell.Value, CultureInfo.InvariantCulture) ?? "");
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }
    Console.WriteLine($"exported spine to {full}: " + string.Join(", ", sections.Select(x => $"{x.rows.Count} {x.name}")) + ".");
}

void SpineImport(string fromPath)
{
    if (!File.Exists(fromPath)) { Console.Error.WriteLine($"no spine file at {fromPath}"); return; }
    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fromPath));
    JsonElement root = doc.RootElement;

    string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
    bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null &&
        (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0));

    bool NodeExists(string nk) => ScalarLong("SELECT count(*) FROM node_now WHERE k=$k", ("$k", nk)) > 0;

    int n = 0, s = 0, l = 0, a = 0;
    Exec("BEGIN");
    if (root.TryGetProperty("nodes", out JsonElement nodes))
        foreach (JsonElement e in nodes.EnumerateArray())
        { AddNode(Str(e, "k"), Str(e, "kind"), Str(e, "label"), Str(e, "gloss"), Str(e, "scheme"), Flag(e, "hard"), "seed"); n++; }

    if (root.TryGetProperty("slots", out JsonElement slots))
        foreach (JsonElement e in slots.EnumerateArray())
        { AddSlot(Str(e, "frame_k"), Str(e, "name"), Str(e, "value"), Str(e, "facet"), Flag(e, "multi"), "", "seed", "seed"); s++; }

    if (root.TryGetProperty("links", out JsonElement links))
        foreach (JsonElement e in links.EnumerateArray())
        {
            string sj = Str(e, "s"), o = Str(e, "o");
            if (!NodeExists(sj) || !NodeExists(o)) { Console.Error.WriteLine($"spine: skip {sj} -> {o} (missing node)"); continue; }
            AddTriple(sj, Str(e, "p"), o, Str(e, "because"), "seed", false, "seed");
            l++;
        }

    if (root.TryGetProperty("aliases", out JsonElement aliases))
        foreach (JsonElement e in aliases.EnumerateArray())
        { Run("INSERT INTO proj_alias(short,k) VALUES($a,$k) ON CONFLICT(short) DO UPDATE SET k=$k", ("$a", Str(e, "short")), ("$k", Str(e, "k"))); a++; }

    if (root.TryGetProperty("terms", out JsonElement terms))
        foreach (JsonElement e in terms.EnumerateArray())
            Run("INSERT OR IGNORE INTO term_alias(term,canonical) VALUES($t,$c)", ("$t", Str(e, "term")), ("$c", Str(e, "canonical")));

    int g = 0;
    if (root.TryGetProperty("edges", out JsonElement edges))
        foreach (JsonElement e in edges.EnumerateArray())
        {
            int line = e.TryGetProperty("line", out JsonElement lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 0;
            int hard = e.TryGetProperty("hardcoded", out JsonElement hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : 0;
            Run("INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,$h)",
                ("$s", Str(e, "symbol")), ("$c", Str(e, "contract")), ("$p", Str(e, "project")),
                ("$f", Str(e, "file")), ("$l", line), ("$u", Str(e, "usage")), ("$h", hard));
            g++;
        }

    Exec("COMMIT");
    Exec("INSERT INTO node_fts(node_fts) VALUES('rebuild')");
    Console.WriteLine($"imported spine: {n} nodes, {s} slots, {l} links, {a} alias(es), {g} edge(s).");
}

void Eval()
{
    (string q, string expect, bool answerable)[] questions =
    {
        ("media base url", "raw.githubusercontent.com", true),
        ("is unauthorized response 401 or 403", "403", true),
        ("how many database contexts are there", "THREE", true),
        ("what is the tv shows table called", "Tvs", true),
        ("default server port", "7626", true),
        ("storage driver types is smb supported", "nfs", true),
        ("video item watch progress field name", "timestamp", true),
        ("signalr hub routes", "videoHub", true),
        ("encoder preset profile field name", "profile_json", true),
        ("windows installer release asset name", "ExampleInstaller", true),
        ("quantumblockchain unicornrecipe zzz", "", false),
        ("weather forecast lovely saturday", "", false),
        ("server", "", false),
        ("list my favorite colors today", "", false),
    };
    int hits = 0;
    double totalMs = 0;
    foreach ((string q, string expect, bool answerable) in questions)
    {
        (List<(string term, string category, string value, string source, double score)> rows, double ms) = Search(q, 1);
        totalMs += ms;
        bool ok;
        string shown;
        if (answerable)
        {
            string top = rows.Count > 0 ? rows[0].value : "(no result)";
            ok = rows.Count > 0 && top.Contains(expect, StringComparison.OrdinalIgnoreCase);
            shown = top.Length <= 56 ? top : top[..56] + "…";
        }
        else
        {
            ok = rows.Count == 0;
            shown = rows.Count == 0 ? "(correctly refused)" : "LEAKED: " + (rows[0].value.Length <= 44 ? rows[0].value : rows[0].value[..44] + "…");
        }
        if (ok) hits++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {ms,5:F2}ms  \"{q}\"  {(answerable ? "expect '" + expect + "'" : "expect REFUSE")}  ->  {shown}");
    }
    Console.WriteLine($"\naccuracy {hits}/{questions.Length} ({100.0 * hits / questions.Length:F0}%)   avg {totalMs / questions.Length:F2}ms");
}

// Seed the real cross-project consumption edges mapped from the monorepo (the AITM graph).
void SeedEdges(string? from = null)
{
    // Curated cross-project edges are one ecosystem's contract map, not engine data. They ride in
    // the same spine file as the nodes so an install brings its own, or brings none.
    string seed = from ?? GetFlag("--from") ?? Path.Combine(AppContext.BaseDirectory, "..", "seeds", "spine.json");
    if (!File.Exists(seed)) { Console.Error.WriteLine($"no spine file at {Path.GetFullPath(seed)}"); return; }

    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(seed));
    if (!doc.RootElement.TryGetProperty("edges", out JsonElement edges)) { Console.WriteLine("spine has no edges section."); return; }

    string S(JsonElement e, string n) => e.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    int I(JsonElement e, string n) => e.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    int n = 0;
    Exec("BEGIN");
    foreach (JsonElement e in edges.EnumerateArray())
    {
        Run("INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,$h)",
            ("$s", S(e, "symbol")), ("$c", S(e, "contract")), ("$p", S(e, "project")),
            ("$f", S(e, "file")), ("$l", I(e, "line")), ("$u", S(e, "usage")), ("$h", I(e, "hardcoded")));
        n++;
    }
    Exec("COMMIT");
    Console.WriteLine($"seeded {n} curated edge(s) from {Path.GetFullPath(seed)}.");
}

// --- Code graph: graph-query / graph-path / graph-explain --------------------------------------
// All three walk the SAME graph the `impact` command reads: `edges` rows, each one a (symbol, file,
// line, project) fact — either a declaration (contract='decl') or a curated usage site. There is no
// separate call-graph table, so "defined-in" and "uses" are both read off this one table.

const int GraphLineCap = 40;

// A LIKE match on the raw symbol column hits substrings anywhere ("stand" inside "TrackerStandsDown").
// Requiring a token to equal the whole symbol or one of its case/underscore-separated parts is what
// keeps a natural-language question from pulling in unrelated symbols that merely contain a query word.
bool SymbolMatchesTokens(string symbol, List<string> toks)
{
    string name = symbol.ToLowerInvariant();
    HashSet<string> parts = new(StringComparer.OrdinalIgnoreCase) { name };
    foreach (string part in Regex.Split(symbol, "_|(?<=[a-z0-9])(?=[A-Z])"))
        if (part.Length > 0) parts.Add(part.ToLowerInvariant());
    return toks.Any(t => parts.Contains(t));
}

string ClipCell(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

// Hard ceiling on total output lines — an agent calling this pays context for every line back.
string CapLines(List<string> lines, int max = GraphLineCap)
{
    if (lines.Count <= max) return string.Join("\n", lines);
    return string.Join("\n", lines.Take(max)) + $"\n(+{lines.Count - max} more line(s) truncated — narrow the query)";
}

// Resolve a loose name to a live graph node: an exact symbol (any case) first, then a file whose path
// ends with the given text. Symbols and files share no namespace, so the first hit wins unambiguously.
(string kind, string value)? ResolveGraphNode(string input)
{
    string norm = input.Trim();
    if (norm.Length == 0) return null;
    string? sym = ScalarText("SELECT symbol FROM edges WHERE symbol=$s COLLATE NOCASE LIMIT 1", ("$s", norm));
    if (sym != null) return ("symbol", sym);
    string tail = norm.Replace('\\', '/');
    string? file = ScalarText("SELECT file FROM edges WHERE file LIKE $f LIMIT 1", ("$f", "%" + tail));
    return file != null ? ("file", file) : null;
}

// Breadth-first search over the symbol<->file bipartite graph edges gives us for free: a symbol's hop
// is its declaration/usage files, a file's hop is the symbols it carries. Depth is capped at 6 hops —
// past that a "connection" is coincidence, not a fact worth reporting — and each node's fan-out is
// capped too, so one generic symbol used everywhere cannot blow up the search.
List<(string toKind, string toVal, string file, int line, string rel)>? GraphBfs((string kind, string value) from, (string kind, string value) to, int maxDepth = 6)
{
    // (symbol,file,line) already exists as edges_ident_idx; the file-first composite is the missing
    // half — without it, "what symbols does this file carry" needs a full scan+sort per file, which is
    // where the whole search was actually spending its time (verified: dropping to COLLATE NOCASE below
    // was the other half of the same mistake — it silently defeated edges_ident_idx on every hop).
    Exec("CREATE INDEX IF NOT EXISTS edges_file_idx ON edges(file, symbol, line)");
    if (from.kind == to.kind && string.Equals(from.value, to.value, StringComparison.OrdinalIgnoreCase)) return new();

    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { $"{from.kind}:{from.value}" };
    List<(string kind, string value, List<(string toKind, string toVal, string file, int line, string rel)> hops)> frontier = new()
    {
        (from.kind, from.value, new List<(string, string, string, int, string)>())
    };

    // A generic symbol (or a file with hundreds of declarations) can fan out into thousands of
    // neighbors; without a hard expansion budget one bad endpoint turns a 2s lookup into a table
    // scan. 2000 node expansions cover any real chain within depth 6 and keeps the worst case fast.
    int expansions = 2000;
    for (int depth = 0; depth < maxDepth; depth++)
    {
        List<(string kind, string value, List<(string toKind, string toVal, string file, int line, string rel)> hops)> next = new();
        foreach ((string kind, string value, List<(string toKind, string toVal, string file, int line, string rel)> hops) cur in frontier)
        {
            if (expansions-- <= 0) return null;
            using SqliteCommand c = db.CreateCommand();
            string rel = cur.kind == "symbol" ? "defined-in" : "uses";
            // No COLLATE NOCASE here: cur.value is always the exact stored casing (it came out of this
            // same table, either from ResolveGraphNode's one-off lookup or a previous hop's own row), so
            // a plain binary equality can use edges_ident_idx / edges_file_idx. Adding NOCASE back here
            // silently forces a full-table scan on every single hop — that was the real cost, not the
            // graph itself: 2000 expansions at a full 127k-row scan each is the 9s regression this fixes.
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

// "Find the most relevant symbols, files and docs for a question" — the code channel's own scoring
// (word-boundary symbol match, grouped by project) plus the same docs_fts lookup `doc` already uses.
void GraphQueryCmd(string question)
{
    List<string> toks = Tokens(question);
    if (toks.Count == 0) { Console.WriteLine("usage: aitm graph-query <question>"); return; }

    HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
    foreach (string t in toks)
    {
        using SqliteCommand c = db.CreateCommand();
        c.CommandText = "SELECT DISTINCT symbol FROM edges WHERE symbol LIKE $p LIMIT 400";
        c.Parameters.AddWithValue("$p", "%" + t + "%");
        using SqliteDataReader r = c.ExecuteReader();
        while (r.Read()) candidates.Add(r.GetString(0));
    }
    List<string> matched = candidates.Where(s => SymbolMatchesTokens(s, toks)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(20).ToList();

    List<string> lines = new();
    if (matched.Count == 0)
    {
        lines.Add($"no symbol matches \"{question}\".");
    }
    else
    {
        // One hop of neighbors per symbol: its declaration file, and who else touches it, by project.
        List<(string symbol, string project, string defLoc, List<(string project, int files)> byProject)> rows = new();
        foreach (string sym in matched)
        {
            string? defLoc = null;
            string homeProject = "";
            using (SqliteCommand c = db.CreateCommand())
            {
                c.CommandText = "SELECT project, file, line FROM edges WHERE symbol=$s AND contract='decl' LIMIT 1";
                c.Parameters.AddWithValue("$s", sym);
                using SqliteDataReader r = c.ExecuteReader();
                if (r.Read()) { homeProject = r.GetString(0); defLoc = $"{r.GetString(1)}:{r.GetInt32(2)}"; }
            }
            List<(string project, int files)> byProject = new();
            using (SqliteCommand c = db.CreateCommand())
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

    string match = BuildMatch(question);
    if (match.Length > 0)
    {
        try
        {
            using SqliteCommand c = db.CreateCommand();
            c.CommandText = @"SELECT d.title, d.path FROM (SELECT k, bm25(docs_fts) AS s FROM docs_fts WHERE docs_fts MATCH $m ORDER BY s LIMIT 3) x
                JOIN docs d ON d.k=x.k ORDER BY x.s";
            c.Parameters.AddWithValue("$m", match);
            using SqliteDataReader r = c.ExecuteReader();
            bool any = false;
            while (r.Read())
            {
                if (!any) { lines.Add("docs:"); any = true; }
                lines.Add($"  • {ClipCell(r.GetString(0), 70)} ({ClipCell(r.GetString(1), 60)})");
            }
        }
        catch (SqliteException) { /* docs not indexed for this instance */ }
    }
    Console.WriteLine(CapLines(lines));
}

// Shortest path between two symbols/files over the code graph — "what connects A to B" without a
// human tracing declaration-then-usage chains by hand.
void GraphPathCmd(string fromInput, string toInput)
{
    (string kind, string value)? from = ResolveGraphNode(fromInput);
    (string kind, string value)? to = ResolveGraphNode(toInput);
    if (from == null) { Console.WriteLine($"no symbol or file matches \"{fromInput}\"."); return; }
    if (to == null) { Console.WriteLine($"no symbol or file matches \"{toInput}\"."); return; }

    List<(string toKind, string toVal, string file, int line, string rel)>? hops = GraphBfs(from.Value, to.Value);
    if (hops == null) { Console.WriteLine($"no path found between \"{fromInput}\" and \"{toInput}\" within depth 6."); return; }
    if (hops.Count == 0) { Console.WriteLine("same node."); return; }

    List<string> lines = new()
    {
        $"[{from.Value.kind}] {from.Value.value}  ->  [{to.Value.kind}] {to.Value.value}   ({hops.Count} hop(s))"
    };
    foreach ((string toKind, string toVal, string file, int line, string rel) h in hops)
    {
        // For a "defined-in"/"uses" hop landing ON a file node, toVal already IS the file — repeating
        // it as "file:line" said the same path twice. Only the line number is new information there.
        string loc = h.toKind == "file" ? $"line {h.line}" : $"{h.file}:{h.line}";
        lines.Add($"  {h.rel}  [{h.toKind}] {h.toVal}   {loc}");
    }
    Console.WriteLine(CapLines(lines));
}

// What a symbol IS (kind + where it's declared), who uses it (grouped by project, top sites), and any
// docs/rules that mention it — the "explain this to me" answer graphify's `explain` gave.
void GraphExplainCmd(string symbol)
{
    if (symbol.Trim().Length == 0) { Console.WriteLine("usage: aitm graph-explain <symbol>"); return; }
    string? resolved = ScalarText("SELECT symbol FROM edges WHERE symbol=$s COLLATE NOCASE LIMIT 1", ("$s", symbol))
        ?? ScalarText("SELECT symbol FROM edges WHERE symbol LIKE $s ORDER BY length(symbol) LIMIT 1", ("$s", "%" + symbol + "%"));
    if (resolved == null) { Console.WriteLine($"no symbol matches \"{symbol}\"."); return; }

    List<string> lines = new();
    List<(string project, string file, int line, string usage)> declRows = new();
    using (SqliteCommand c = db.CreateCommand())
    {
        c.CommandText = "SELECT project, file, line, usage FROM edges WHERE symbol=$s AND contract='decl' ORDER BY project, file LIMIT 5";
        c.Parameters.AddWithValue("$s", resolved);
        using SqliteDataReader r = c.ExecuteReader();
        while (r.Read()) declRows.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
    }
    string kind = declRows.Count > 0 ? declRows[0].usage : (ScalarText("SELECT contract FROM edges WHERE symbol=$s AND contract != '' LIMIT 1", ("$s", resolved)) ?? "unknown");
    lines.Add($"{resolved}  [{kind}]");
    if (declRows.Count == 0) lines.Add("  defined: (no indexed declaration — curated usage edge only, or not yet indexed)");
    foreach ((string project, string file, int line, string usage) d in declRows)
        lines.Add($"  defined: {d.project}  {d.file}:{d.line}");

    // Known-issue fix (RESTRUCTURE.md slice 13): contract != 'decl' excludes a symbol's own declaration
    // row from its "used by" count — a declaration is not a use, and is already reported above as "defined:".
    List<(string project, int sites, int files)> byProject = new();
    using (SqliteCommand c = db.CreateCommand())
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
    using (SqliteCommand c = db.CreateCommand())
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
            lines.Add($"  {r.GetString(0),-10} {r.GetString(1)}:{r.GetInt32(2)}{(hard ? " [HARDCODED]" : "")}  {ClipCell(r.GetString(3), 60)}");
        }
        if (n == 0) lines.Add("  (none recorded)");
    }

    // AITM's edges table records where a symbol is DECLARED and USED, not what it itself calls — there
    // is no call-graph edge to read, so this is honest about the gap rather than guessing from co-location.
    lines.Add("uses: not tracked — the edges table records declaration/usage sites, not call relationships.");

    string match = BuildMatch(resolved);
    if (match.Length > 0)
    {
        try
        {
            using SqliteCommand c = db.CreateCommand();
            c.CommandText = @"SELECT title, path FROM (SELECT k, bm25(docs_fts) AS s FROM docs_fts WHERE docs_fts MATCH $m ORDER BY s LIMIT 2) x
                JOIN docs d ON d.k=x.k ORDER BY x.s";
            c.Parameters.AddWithValue("$m", match);
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) lines.Add($"  doc: {ClipCell(r.GetString(0), 60)} ({ClipCell(r.GetString(1), 50)})");
        }
        catch (SqliteException) { /* docs not indexed */ }
        try
        {
            using SqliteCommand c = db.CreateCommand();
            c.CommandText = @"SELECT hook FROM (SELECT k, bm25(memory_fts) AS s FROM memory_fts WHERE memory_fts MATCH $m ORDER BY s LIMIT 2) x
                JOIN memory mem ON mem.k=x.k ORDER BY x.s";
            c.Parameters.AddWithValue("$m", match);
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) lines.Add($"  rule: {ClipCell(r.GetString(0), 70)}");
        }
        catch (SqliteException) { /* memory not indexed */ }
    }
    Console.WriteLine(CapLines(lines));
}

// Prune heavy dirs DURING the walk (never descend into node_modules); tolerate unreadable dirs.
IEnumerable<string> EnumerateSource(string root, string[] patterns)
{
    // .scratch and friends hold staged COPIES of real packages. Indexed, a staging copy wins the upsert
    // and the store then reports a package's home as the scratch directory it was last built into.
    string[] skip =
    {
        "node_modules", "dist", "build", "bin", "obj", ".git", ".nuxt", ".gradle", "vendor", ".idea", ".vs",
        ".scratch", ".turbo", ".next", ".output", ".svelte-kit", "coverage", "out", "target", "__pycache__",
    };
    Stack<string> stack = new();
    stack.Push(root);
    while (stack.Count > 0)
    {
        string dir = stack.Pop();
        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { subs = Array.Empty<string>(); }
        foreach (string sub in subs)
            if (!skip.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
        foreach (string pattern in patterns)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, pattern); }
            catch { files = Array.Empty<string>(); }
            foreach (string file in files) yield return file;
        }
    }
}

// Whole-token match: the symbol must not be flanked by identifier chars, so "data" never matches "metadata".
bool ContainsToken(string line, string symbol)
{
    int i = 0;
    while ((i = line.IndexOf(symbol, i, StringComparison.Ordinal)) >= 0)
    {
        bool leftOk = i == 0 || !IsIdent(line[i - 1]);
        int end = i + symbol.Length;
        bool rightOk = end >= line.Length || !IsIdent(line[end]);
        if (leftOk && rightOk) return true;
        i = end;
    }
    return false;
}

static bool IsIdent(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

// Promote a reviewed candidate into the curated graph through the same diff-and-log path as seed-edges.
void PromoteCandidate(int id)
{
    string sym, contract, proj, file, usage;
    int line, hard;
    using (SqliteCommand c = db.CreateCommand())
    {
        c.CommandText = "SELECT symbol,contract,project,file,line,usage,hardcoded FROM edge_candidates WHERE id=$i AND status='pending'";
        c.Parameters.AddWithValue("$i", id);
        using SqliteDataReader r = c.ExecuteReader();
        if (!r.Read()) { Console.WriteLine($"no pending candidate #{id}."); return; }
        sym = r.GetString(0); contract = r.GetString(1); proj = r.GetString(2);
        file = r.GetString(3); line = r.GetInt32(4); usage = r.GetString(5); hard = r.GetInt32(6);
    }
    long exists = ScalarLong("SELECT count(*) FROM edges WHERE symbol=$s AND project=$p AND file=$f AND line=$l",
        ("$s", sym), ("$p", proj), ("$f", file), ("$l", line));
    Exec("BEGIN");
    if (exists == 0)
    {
        Run("INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,$h)",
            ("$s", sym), ("$c", contract), ("$p", proj), ("$f", file), ("$l", line), ("$u", usage), ("$h", hard));
        LogMutation("edge", $"{sym}|{proj}|{file}|{line}", "insert", null, $"{contract}:{usage}", $"promote:#{id}");
    }
    Run("UPDATE edge_candidates SET status='promoted' WHERE id=$i", ("$i", id));
    Exec("COMMIT");
    Console.WriteLine(exists == 0 ? $"promoted #{id}: {sym} -> {proj} {file}:{line} (now in the graph)." : $"#{id} already in graph; marked promoted.");
}

// TDD harness: assertions over the foundation's invariants. It resets tables, so it refuses any
// instance not prefixed "test" — the real store is never touched.
void SelfTest()
{
    if (!instance.StartsWith("test", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("selftest refuses to run outside a 'test*' instance (it resets tables). Use: aitm selftest --instance test");
        Environment.Exit(2);
    }
    Exec("PRAGMA foreign_keys=OFF"); // node has a self-FK (superseded_by); drop enforcement only for the bulk reset
    foreach (string t in new[] { "facts", "facts_fts", "mutations", "edges", "todos", "findings", "chat", "chat_fts", "edge_candidates", "projects", "docs", "docs_fts", "memory", "memory_fts", "ref", "triple", "slot", "usage", "distill_log", "node", "gaps", "meta" }) Exec($"DELETE FROM {t}");
    Exec("PRAGMA foreign_keys=ON");

    int pass = 0, fail = 0;
    void Check(string name, bool ok)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (ok) pass++; else fail++;
    }
    long Count(string sql) => (long)(new SqliteCommand(sql, db).ExecuteScalar() ?? 0L);

    Exec("BEGIN");
    UpsertFact("port", "default server port", "[]", "config", "7626", "docs", "", "selftest");
    Exec("COMMIT");
    (List<(string term, string category, string value, string source, double score)> hit, _) = Search("default server port", 1);
    Check("knowledge: stored fact is retrievable", hit.Count == 1 && hit[0].value == "7626");

    (List<(string term, string category, string value, string source, double score)> none, _) = Search("quantumblockchain unicornrecipe zzz", 1);
    Check("refusal: nonsense query returns nothing", none.Count == 0);

    LogGap("query", "quantumblockchain unicornrecipe zzz");
    LogGap("query", "quantumblockchain unicornrecipe zzz");
    Check("gaps: refused query is logged once with misses=2",
        Count("SELECT count(*) FROM gaps WHERE status='open'") == 1
        && Count("SELECT misses FROM gaps WHERE query LIKE '%quantumblockchain%'") == 2);
    ResolveGaps("quantumblockchain is the unicornrecipe zzz answer we learned");
    Check("gaps: a covering learn auto-resolves the gap", Count("SELECT count(*) FROM gaps WHERE status='filled'") == 1);
    ResolveGaps("unrelated text");
    Check("gaps: non-covering learn resolves nothing new", Count("SELECT count(*) FROM gaps WHERE status='filled'") == 1);

    BrainLearn(new List<string> { "node", "scram:x", "This sentence is not a kind token", "some label" });
    Check("guard: prose node kind is rejected", Count("SELECT count(*) FROM node_now WHERE k='scram:x'") == 0);
    BrainLearn(new List<string> { "node", "scram:y", "fact", string.Join(" ", Enumerable.Repeat("word", 40)) });
    Check("guard: paragraph node label is rejected", Count("SELECT count(*) FROM node_now WHERE k='scram:y'") == 0);
    BrainLearn(new List<string> { "node", "scram:ok", "fact", "short clean label" });
    Check("guard: well-formed node still writes", Count("SELECT count(*) FROM node_now WHERE k='scram:ok'") == 1);

    Exec("BEGIN");
    UpsertFact("port", "default server port", "[]", "config", "9999", "docs", "", "selftest-update");
    Exec("COMMIT");
    (List<(string term, string category, string value, string source, double score)> latest, _) = Search("default server port", 1);
    Check("current: projection reflects latest value only", latest.Count == 1 && latest[0].value == "9999");
    Check("history: cold log retains both fact mutations", Count("SELECT count(*) FROM mutations WHERE kind='fact' AND k='port'") == 2);

    // seeds/ is gitignored operator data, so a clean checkout (CI, a worktree) has no spine.json. The
    // selftest writes the one edge it checks rather than passing only where that private file exists.
    string fixtureSpine = Path.Combine(root, "selftest-spine.json");
    File.WriteAllText(fixtureSpine, """
        {"edges":[{"symbol":"has_more","contract":"PaginatedResponse","project":"test-a","file":"src/list.ts","line":1,"usage":"page.has_more","hardcoded":1}]}
        """);
    SeedEdges(fixtureSpine);
    Check("impact: has_more is flagged hardcoded", Count("SELECT count(*) FROM edges WHERE symbol='has_more' AND hardcoded=1") >= 1);

    long edgeMutsBefore = Count("SELECT count(*) FROM mutations WHERE kind='edge'");
    SeedEdges(fixtureSpine);
    Check("edges: re-sync is idempotent (no new mutations)", Count("SELECT count(*) FROM mutations WHERE kind='edge'") == edgeMutsBefore);

    Exec("BEGIN");
    Run("INSERT INTO todos(ts,title,status,why) VALUES($t,'t1','open','')", ("$t", Now()));
    LogMutation("todo", "t1", "insert", null, "open", "");
    Exec("COMMIT");
    int tid = (int)Count("SELECT id FROM todos WHERE title='t1'");
    CloseTodo(tid);
    Check("todos: open and done both logged", Count("SELECT count(*) FROM mutations WHERE kind='todo'") == 2);
    long todoMutsBefore = Count("SELECT count(*) FROM mutations WHERE kind='todo'");
    CloseTodo(99999);
    Check("guard: closing a nonexistent todo is a no-op (no phantom mutation)", Count("SELECT count(*) FROM mutations WHERE kind='todo'") == todoMutsBefore);

    Run("INSERT INTO chat(k,session,ts,role,text) VALUES('s:1','s','t','user',$tx)", ("$tx", "the operator said never use optionalDependencies in package json"));
    Run("INSERT INTO chat_fts(k,text) VALUES('s:1',$tx)", ("$tx", "the operator said never use optionalDependencies in package json"));
    Check("recall: indexed chat message is retrievable", Count("SELECT count(*) FROM chat_fts WHERE chat_fts MATCH 'optionaldependencies'") == 1);
    (List<(string term, string category, string value, string source, double score)> factSide, _) = Search("optionaldependencies", 1);
    Check("isolation: chat never leaks into the verified-facts channel", factSide.Count == 0);

    Check("extract: token match respects word boundaries", !ContainsToken("const metadata = 1", "data") && ContainsToken("row.data = 1", "data"));
    Run("INSERT INTO edge_candidates(symbol,contract,project,file,line,usage,hardcoded,status) VALUES('foo_field','TestDto','web','src/x.ts',10,'a.foo_field',1,'pending')");
    int cid = (int)Count("SELECT id FROM edge_candidates WHERE symbol='foo_field'");
    PromoteCandidate(cid);
    Check("promote: candidate enters the curated graph", Count("SELECT count(*) FROM edges WHERE symbol='foo_field'") == 1);
    Check("promote: insert is logged to the cold edge log", Count("SELECT count(*) FROM mutations WHERE kind='edge' AND k LIKE 'foo_field%'") == 1);
    Check("promote: candidate is marked promoted", Count($"SELECT count(*) FROM edge_candidates WHERE id={cid} AND status='promoted'") == 1);

    // Relevance floor: with a populated corpus, a distinctive term hits but a generic low-IDF term is refused.
    Exec("BEGIN");
    for (int n = 0; n < 22; n++) UpsertFact($"f{n}", $"filler concept {n}", "[]", "misc", $"value number {n} alpha", "seed", "", "selftest-corpus");
    UpsertFact("distinct", "spritevtt muxer crash", "[]", "bug", "spritevtt eagain in multi output pipeline", "seed", "", "selftest-corpus");
    Exec("COMMIT");
    (List<(string term, string category, string value, string source, double score)> distinctive, _) = Search("spritevtt", 1);
    Check("floor: distinctive term still retrieves in a populated corpus", distinctive.Count == 1);
    (List<(string term, string category, string value, string source, double score)> generic, _) = Search("value", 1);
    Check("floor: generic low-IDF term is refused once corpus is large", generic.Count == 0);

    (List<(string term, string category, string value, string source, double score)> covGood, _) = Search("spritevtt muxer crash", 1);
    Check("coverage: query whose tokens mostly match retrieves", covGood.Count == 1);
    (List<(string term, string category, string value, string source, double score)> covBad, _) = Search("spritevtt rabbit telescope", 1);
    Check("coverage: 3+ token query matching one tangential word is refused", covBad.Count == 0);

    Run("INSERT INTO docs(k,path,title,category,content) VALUES('d:1','p/x.md','Auth Spec','spec','idp token exchange pkce flow')");
    Run("INSERT INTO docs_fts(k,title,content) VALUES('d:1','Auth Spec','idp token exchange pkce flow')");
    Check("docs: absorbed section is retrievable", Count("SELECT count(*) FROM docs_fts WHERE docs_fts MATCH 'pkce'") == 1);
    ShedDoc("p/x.md");
    Check("shed-doc: removes the section from the docs channel", Count("SELECT count(*) FROM docs WHERE k='d:1'") == 0);

    // Every repo, package and folder in this project is named in kebab-case. Gluing those into one word
    // meant the store could not be asked about anything by the name it actually goes by.
    Check("tokens: a kebab-case name splits into its words", Tokens("the-effortless-encoder").SequenceEqual(new[] { "effortless", "encoder" }));
    Check("tokens: a path splits on separators", Tokens("apps/nomercy-app-web/src").Contains("nomercy") && Tokens("apps/nomercy-app-web/src").Contains("web"));

    // The name of a thing usually lives in its folder rather than its prose, so the path has to be
    // searchable text — a report that never writes its own title was otherwise unreachable by title.
    string pterms = PathTerms("c:/repo/docs/reports/the-effortless-encoder/04-safety-net.md");
    Check("path terms: folder words become searchable", pterms.Contains("effortless") && pterms.Contains("encoder") && pterms.Contains("safety"));
    Check("path terms: generic and numeric segments are dropped", !pterms.Contains("docs") && !pterms.Contains("04"));

    UpsertFact("junk:shed-me", "junk shed me", "[]", "misc", "temporary fact for the shed-fact test", "seed", "", "selftest");
    Check("shed-fact: fact is retrievable before shedding", Count("SELECT count(*) FROM facts WHERE k='junk:shed-me'") == 1);
    ShedFact("junk:shed-me");
    Check("shed-fact: removes the fact from the facts channel", Count("SELECT count(*) FROM facts WHERE k='junk:shed-me'") == 0);
    Check("shed-fact: removes the fact from the FTS mirror", Count("SELECT count(*) FROM facts_fts WHERE k='junk:shed-me'") == 0);

    Exec("BEGIN");
    UpsertMemory("no-useless-comments", "feedback", "No useless comments — hard rule", "default to zero comments; only flag a hidden constraint", "comments must earn their place; never describe what the code does", "feedback_short_comments", true, "selftest");
    UpsertMemory("offline-server-picker", "project", "Offline server picker", "server offline screen lets multi-server users switch", "switch server not just retry", "", false, "selftest");
    Exec("COMMIT");
    Check("memory: stored rule is retrievable by content", Count("SELECT count(*) FROM memory_fts WHERE memory_fts MATCH 'comments'") == 1);
    Check("memory: hard-rule flag persists for the always-on core", Count("SELECT count(*) FROM memory WHERE hard=1") == 1);
    (List<(string term, string category, string value, string source, double score)> memSide, _) = Search("comments", 1);
    Check("memory: isolated from the verified-facts channel", memSide.Count == 0);
    UpsertMemory("no-useless-comments", "feedback", "No useless comments — hard rule", "default to zero comments; only flag a hidden constraint", "comments must earn their place; never describe what the code does", "feedback_short_comments", true, "selftest");
    Check("memory: re-index of an unchanged rule logs no phantom mutation", Count("SELECT count(*) FROM mutations WHERE kind='memory' AND k='no-useless-comments'") == 1);

    // BRAIN graph — the structured copy of the mental model.
    bool BadRejected(Action act) { try { act(); return false; } catch (SqliteException) { return true; } }
    Exec("BEGIN");
    AddNode("proj:test-a", "project", "Test A", "app A", "", false, "selftest");
    AddNode("proj:test-b", "project", "Test B", "app B", "", false, "selftest");
    AddNode("seam:test-auth", "seam", "Test Auth", "shared authentication seam", "auth", true, "selftest");
    AddTriple("proj:test-a", "consumes", "seam:test-auth", "", "selftest", false, "selftest");
    AddTriple("proj:test-b", "consumes", "seam:test-auth", "", "selftest", false, "selftest");
    Exec("COMMIT");
    Check("brain: node retrievable via porter FTS (stemmed)", Count("SELECT count(*) FROM node_fts WHERE node_fts MATCH 'authentication'") >= 1);
    Check("brain: predicate guard rejects an unknown predicate", BadRejected(() => Run("INSERT INTO triple(s,p,o,o_is_literal) VALUES('proj:test-a','consoom','seam:test-auth',0)")));
    Check("brain: dangling subject is a write error, not a silent miss", BadRejected(() => Run("INSERT INTO triple(s,p,o,o_is_literal) VALUES('proj:ghost','consumes','seam:test-auth',0)")));
    Check("brain: intersection finds the shared seam in one query",
        Count("SELECT count(*) FROM (SELECT t.o FROM triple_now t WHERE t.p='consumes' AND t.s IN ('proj:test-a','proj:test-b') GROUP BY t.o HAVING COUNT(DISTINCT t.s)=2)") == 1);
    Exec("BEGIN"); AddNode("proj:test-a", "project", "Test A", "app A v2", "", false, "selftest-update"); Exec("COMMIT");
    Check("brain: supersede keeps one live node + full history", Count("SELECT count(*) FROM node WHERE k='proj:test-a'") == 2 && Count("SELECT count(*) FROM node_now WHERE k='proj:test-a'") == 1);
    Exec("BEGIN"); AddNode("proj:test-b", "project", "Test B", "app B", "", false, "selftest-noop"); Exec("COMMIT");
    Check("brain: re-adding an unchanged node is a no-op (no phantom version)", Count("SELECT count(*) FROM node WHERE k='proj:test-b'") == 1);

    // BRAIN learning loop — reinforce-on-use, usage-blended recall ranking, confirmation strengthens belief.
    Exec("BEGIN");
    AddNode("node:rank-a", "concept", "zorptron widget", "zorptron widget gizmo", "", false, "selftest");
    AddNode("node:rank-b", "concept", "zorptron widget", "zorptron widget gizmo", "", false, "selftest");
    Reinforce(new[] { "node:rank-a" });
    Reinforce(new[] { "node:rank-a", "not-a-node" });
    Exec("COMMIT");
    Check("learn: reinforce raises hits + sets recency for a live node", Count("SELECT hits FROM usage WHERE node_k='node:rank-a'") == 2 && Count("SELECT count(*) FROM usage WHERE node_k='node:rank-a' AND last_used IS NOT NULL") == 1);
    Check("learn: reinforce ignores non-node keys (no junk usage rows)", Count("SELECT count(*) FROM usage WHERE node_k='not-a-node'") == 0);
    string topRanked = ScalarText(@"SELECT n.k FROM node_fts f JOIN node_now n ON n.id=f.rowid LEFT JOIN usage u ON u.node_k=n.k
        WHERE node_fts MATCH 'zorptron'
        ORDER BY bm25(node_fts) - 0.15*MIN(COALESCE(u.hits,0),20)
                 - CASE WHEN u.last_used IS NULL THEN 0 WHEN julianday('now')-julianday(u.last_used)<1 THEN 0.6 ELSE 0 END LIMIT 1") ?? "";
    Check("learn: usage-blended recall ranks the reinforced node above an equal-relevance one", topRanked == "node:rank-a");
    Exec("BEGIN");
    AddTriple("proj:test-b", "related", "proj:test-a", "", "selftest", false, "selftest");
    AddTriple("proj:test-b", "related", "proj:test-a", "", "selftest", false, "selftest");
    Exec("COMMIT");
    Check("learn: re-asserting a triple strengthens conf without duplicating",
        Count("SELECT count(*) FROM triple_now WHERE s='proj:test-b' AND p='related' AND o='proj:test-a'") == 1
        && Count("SELECT count(*) FROM triple_now WHERE s='proj:test-b' AND p='related' AND o='proj:test-a' AND conf>1.0") == 1);

    // recall: an alias canonical with a hyphen (screen -> compose-screen) must be QUOTED into the FTS5 match,
    // else '-' parses as the NOT operator and the whole query throws. Regression guard for that crash.
    Run("INSERT INTO node(k,kind,label,gloss,hard) VALUES('kind:hyp-test','codekind','hyphen test','compose-screen widget',0)");
    Check("recall: hyphenated alias expansion is quoted (no FTS5 syntax crash)",
        Count("SELECT count(*) FROM (WITH expanded(term) AS (VALUES('screen'),('compose-screen')), me AS (SELECT group_concat('\"'||term||'\"',' OR ') AS m FROM expanded) SELECT 1 FROM node_fts WHERE node_fts MATCH (SELECT m FROM me))") >= 1);

    // hub -> signalr is ecosystem vocabulary: it left the built-in aliases in 291c7cc and arrives through
    // spine-import. A store made before that kept the old row (the reset list never clears term_alias),
    // which hid that a fresh instance had no alias to expand. The selftest adds the one it checks.
    Run("INSERT OR IGNORE INTO term_alias(term,canonical) VALUES('hub','signalr')");
    Run("INSERT INTO node(k,kind,label,gloss,hard) VALUES('seam:hubtest','seam','Realtime thing','signalr realtime sync',0)");
    Check("recall: a synonym alias expands the query (hub -> signalr)",
        Count("SELECT count(*) FROM (WITH q(raw) AS (VALUES('hub')), expanded AS (SELECT raw AS term FROM q UNION SELECT a.canonical FROM q JOIN term_alias a ON a.term=q.raw), me AS (SELECT group_concat('\"'||term||'\"',' OR ') AS m FROM expanded) SELECT 1 FROM node_fts WHERE node_fts MATCH (SELECT m FROM me))") >= 1);

    Run("INSERT INTO node(k,kind,label,gloss,hard) VALUES('contract:envtest','contract','Envelope','DataResponseDto wrapper',0)");
    Check("recall: substring fallback catches what FTS misses (dataresponse)",
        Count("SELECT count(*) FROM node_fts WHERE node_fts MATCH '\"dataresponse\"'") == 0
        && Count("SELECT count(*) FROM node_now WHERE label LIKE '%dataresponse%' OR gloss LIKE '%dataresponse%'") >= 1);

    // merge: a duplicate folds into the canonical node, re-pointing edges with no dead reference left
    Exec("BEGIN");
    AddNode("dup:src", "concept", "Dup Source", "", "", false, "selftest");
    AddNode("dup:dst", "concept", "Dup Target", "", "", false, "selftest");
    AddTriple("proj:test-a", "related", "dup:src", "", "selftest", false, "selftest");
    Exec("COMMIT");
    BrainMerge("dup:src", "dup:dst");
    Check("merge: source retired, edge re-pointed, no dead reference",
        Count("SELECT count(*) FROM node_now WHERE k='dup:src'") == 0
        && Count("SELECT count(*) FROM triple_now WHERE s='proj:test-a' AND p='related' AND o='dup:dst'") == 1
        && Count("SELECT count(*) FROM triple_now WHERE o='dup:src'") == 0);

    // forget: a node and its live edges retire together so the audit stays clean
    Exec("BEGIN");
    AddNode("gone:x", "concept", "Gone", "", "", false, "selftest");
    AddTriple("proj:test-b", "related", "gone:x", "", "selftest", false, "selftest");
    Exec("COMMIT");
    BrainForget("gone:x");
    Check("forget: node + its edges retired (no orphaned reference)",
        Count("SELECT count(*) FROM node_now WHERE k='gone:x'") == 0 && Count("SELECT count(*) FROM triple_now WHERE o='gone:x'") == 0);

    // contradiction: conflicting predicates on the same pair are detectable
    Exec("BEGIN");
    AddNode("c:s", "concept", "CS", "", "", false, "selftest");
    AddNode("c:o", "rule", "CO", "", "", false, "selftest");
    AddTriple("c:s", "requires", "c:o", "", "selftest", false, "selftest");
    AddTriple("c:s", "forbids", "c:o", "", "selftest", false, "selftest");
    Exec("COMMIT");
    Check("conflicts: requires+forbids on the same pair is detected",
        Count("SELECT count(*) FROM (SELECT DISTINCT t1.s FROM triple_now t1 JOIN pred_vocab v ON v.p=t1.p AND v.conflicts IS NOT NULL JOIN triple_now t2 ON t2.s=t1.s AND t2.o=t1.o AND t2.p=v.conflicts WHERE t1.s='c:s')") == 1);

    BrainVerify("c:s");
    Check("verify: confirmation timestamp recorded", Count("SELECT count(*) FROM usage WHERE node_k='c:s' AND verified_at IS NOT NULL") == 1);

    // Code graph: graph-query / graph-path / graph-explain, against a tiny fixture, not the real repo tree.
    string Capture(Action act)
    {
        StringWriter sw = new();
        TextWriter prev = Console.Out;
        Console.SetOut(sw);
        try { act(); } finally { Console.SetOut(prev); }
        return sw.ToString();
    }
    void InsertEdge(string symbol, string contract, string project, string file, int line, string usage) =>
        Run("INSERT INTO edges(symbol,contract,project,file,line,usage,hardcoded) VALUES($s,$c,$p,$f,$l,$u,0)",
            ("$s", symbol), ("$c", contract), ("$p", project), ("$f", file), ("$l", line), ("$u", usage));

    Exec("BEGIN");
    // A symbol declared once and used from two other projects — the case graph-explain's "grouped by
    // project" answer exists to summarise.
    InsertEdge("GraphFixtureWidget", "decl", "alpha", "alpha/widget.ts", 10, "ts declaration");
    InsertEdge("GraphFixtureWidget", "", "beta", "beta/x.ts", 5, "uses widget");
    InsertEdge("GraphFixtureWidget", "", "beta", "beta/z.ts", 1, "uses widget again");
    InsertEdge("GraphFixtureWidget", "", "gamma", "gamma/y.ts", 9, "uses widget too");
    // Two symbols that share one file — exactly a 2-hop path (symbol -> file -> symbol).
    InsertEdge("GraphPathFixtureA", "decl", "alpha", "pathfixture/shared.ts", 1, "ts declaration");
    InsertEdge("GraphPathFixtureB", "decl", "alpha", "pathfixture/shared.ts", 2, "ts declaration");
    // A chain 8 hops long (sym0-f0-sym1-f1-sym2-f2-sym3-f3-sym4): past the depth-6 cutoff on purpose.
    InsertEdge("ChainSym0", "decl", "chain", "chain/f0.ts", 1, "ts declaration");
    InsertEdge("ChainSym1", "decl", "chain", "chain/f0.ts", 2, "ts declaration");
    InsertEdge("ChainSym1", "", "chain", "chain/f1.ts", 1, "uses");
    InsertEdge("ChainSym2", "decl", "chain", "chain/f1.ts", 2, "ts declaration");
    InsertEdge("ChainSym2", "", "chain", "chain/f2.ts", 1, "uses");
    InsertEdge("ChainSym3", "decl", "chain", "chain/f2.ts", 2, "ts declaration");
    InsertEdge("ChainSym3", "", "chain", "chain/f3.ts", 1, "uses");
    InsertEdge("ChainSym4", "decl", "chain", "chain/f3.ts", 2, "ts declaration");
    Exec("COMMIT");

    string gq = Capture(() => GraphQueryCmd("GraphFixtureWidget"));
    Check("graph-query: finds the fixture symbol", gq.Contains("GraphFixtureWidget"));
    Check("graph-query: shows its declaration site", gq.Contains("alpha/widget.ts:10"));
    Check("graph-query: groups the match under its home project", gq.Contains("[alpha]"));

    string gpShort = Capture(() => GraphPathCmd("GraphPathFixtureA", "GraphPathFixtureB"));
    Check("graph-path: finds the 2-hop path", gpShort.Contains("(2 hop(s))"));
    string gpNone = Capture(() => GraphPathCmd("ChainSym0", "ChainSym4"));
    Check("graph-path: reports no path past depth 6", gpNone.Contains("no path found") && gpNone.Contains("depth 6"));
    string gpSame = Capture(() => GraphPathCmd("GraphFixtureWidget", "GraphFixtureWidget"));
    Check("graph-path: same node short-circuits", gpSame.Contains("same node"));
    Check("graph-path: unresolvable name refuses cleanly", Capture(() => GraphPathCmd("NoSuchSymbolAtAll", "GraphFixtureWidget")).Contains("no symbol or file matches"));

    string ge = Capture(() => GraphExplainCmd("GraphFixtureWidget"));
    Check("graph-explain: reports the declaration kind and site", ge.Contains("ts declaration") && ge.Contains("alpha/widget.ts:10"));
    Check("graph-explain: lists users grouped by project", ge.Contains("beta") && ge.Contains("2 site(s)") && ge.Contains("gamma") && ge.Contains("1 site(s)"));

    Console.WriteLine($"\nselftest: {pass} passed, {fail} failed ({(fail == 0 ? "GREEN" : "RED")}).");
    if (fail > 0) Environment.ExitCode = 1;
}
