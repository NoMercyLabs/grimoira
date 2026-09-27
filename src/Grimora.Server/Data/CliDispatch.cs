using System.Globalization;
using Grimora.Brain.Tools;
using Grimora.Docs.Tools;
using Grimora.Facts.Tools;
using Grimora.Graph.Tools;
using Grimora.Memory.Tools;
using Grimora.Store.Data;
using Grimora.Store.Tools;
using Microsoft.Data.Sqlite;

namespace Grimora.Server.Data;

// RESTRUCTURE.md slice 29b: "grimora.cs's dispatch body moves into CliDispatch.Run(string[] args, string
// cwd, TextWriter stdout, TextWriter stderr) -> int in src/Grimora.Server/Data/ ... Every Environment.Exit(n)
// becomes return n." This is grimora.cs's body as it stood, moved here unchanged except for three things:
// output goes to the given writers instead of Console, the current directory is the given cwd, and an
// error exit returns its code, because inside the server an Environment.Exit would stop the server.
// grimora.cs is now a shim that calls this. Lives in Grimora.Server (level 4, ReferenceDirectionTests), the
// one project that may see every feature; a file-based app referencing this Web SDK project was checked
// to build and run without starting Kestrel (Program's top-level statements are not its entry point).
public static class CliDispatch
{
    public static int Run(string[] args, string cwd, TextWriter stdout, TextWriter stderr)
        => new Dispatcher(args, cwd, stdout, stderr, null, null, null).Dispatch();

    /// <summary>Slice 29c: the server form for <c>POST /cli</c>. The project is already resolved from the
    /// request (<paramref name="instance"/>), the store root is the server's data directory, and the verb
    /// runs on the project's one open connection (<see cref="ProjectHandle.Connection"/>, held under its
    /// gate by the caller) instead of opening a second connection of its own. The process environment
    /// (GRIMORA_INSTANCE, CLAUDE_PROJECT_DIR) is never read, so the server's env cannot pin a request.</summary>
    internal static int RunOnStore(string[] args, string cwd, TextWriter stdout, TextWriter stderr,
        string instance, string dataDir, SqliteConnection connection)
        => new Dispatcher(args, cwd, stdout, stderr, instance, dataDir, connection).Dispatch();

    /// <summary>The value of a <c>--flag value</c> pair: the token after the flag, whatever it is, or null
    /// when the flag is absent or last. /cli reads <c>--instance</c> with the same rule the verbs use.</summary>
    internal static string? FlagValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private sealed class Dispatcher(string[] a, string cwd, TextWriter stdout, TextWriter stderr,
        string? fixedInstance, string? dataDir, SqliteConnection? shared)
    {
        private string _instance = "";
        private string _root = "";
        private string _dbPath = "";
        private SqliteConnection _db = null!;

        private readonly HashSet<string> _stop = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "is", "a", "an", "of", "to", "in", "on", "for", "and", "or", "what", "how", "are", "does",
            "do", "it", "its", "be", "this", "that", "with", "as", "at", "by", "my", "i", "you", "we", "there",
            "was", "were", "which", "when", "where", "name", "called", "get", "got", "me", "us", "about",
        };

        public int Dispatch()
        {
            string cmd = a.Length > 0 ? a[0] : "help";
            _instance = fixedInstance ?? GetFlag("--instance") ?? ResolveInstance();
            _root = Path.Combine(dataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grimora"), _instance);
            Directory.CreateDirectory(_root);
            _dbPath = Path.Combine(_root, "grimora.db");

            // The server passes the project's open connection (pragmas already applied by StoreConnection.Open);
            // it is the gate's, so it is not disposed here. The CLI opens and owns its own, as before.
            using SqliteConnection? owned = shared is null ? new($"Data Source={_dbPath};Foreign Keys=True") : null;
            _db = shared ?? owned!;
            if (owned is not null) _db.Open();
            // SQLite serialises writers, and Init/InitBrain below are DDL, so even `query` takes the write lock.
            // Without a busy timeout a momentary writer (session indexer, MCP server, a second CLI) makes the
            // command throw SQLITE_BUSY and the process hard-crash, so callers read the store as broken and fall
            // back to grepping the tree. Wait for the writer instead of dying.
            // The indexer can hold the write lock for minutes, and 5 seconds was not enough: `grimora add` died with
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
            const int schemaVersion = 3;
            if (ScalarLong("PRAGMA user_version") != schemaVersion)
            {
                Init();
                InitBrain();
                Exec($"PRAGMA user_version={schemaVersion}");
            }



            try
            {
                switch (cmd)
                {
                    case "init":
                        stdout.WriteLine(new InitTool().Execute(_instance, _dbPath));
                        break;
                    case "import":
                        stdout.WriteLine(new ImportTool().Execute(_db,
                            GetFlag("--from") ?? throw new ArgumentException("import needs --from <sqlite path>"), _dbPath));
                        break;
                    case "query":
                        stdout.WriteLine(new QueryTool(new UsageSignal()).ExecuteCli(_db, string.Join(' ', Positionals().Skip(1))));
                        break;
                    case "index-chat":
                        stdout.WriteLine(new IndexChatTool().Execute(_db,
                            GetFlag("--from") ?? throw new ArgumentException("index-chat needs --from <session.jsonl | transcript dir>")));
                        break;
                    case "index-packages":
                        // --from is what every other index-* command takes; accepting only --root here silently
                        // indexed the current directory instead of the one that was asked for.
                        stdout.WriteLine(new IndexPackagesTool().Execute(_db, GetFlag("--root") ?? GetFlag("--from") ?? cwd));
                        break;
                    case "index-docs":
                        stdout.WriteLine(new IndexDocsTool().Execute(_db,
                            GetFlag("--from") ?? throw new ArgumentException("index-docs needs --from <dir|file.md>"), GetFlag("--category") ?? "doc"));
                        break;
                    case "doc":
                        stdout.WriteLine(new DocTool().ExecuteCli(_db, string.Join(' ', Positionals().Skip(1))));
                        break;
                    case "index-memory":
                        stdout.WriteLine(new IndexMemoryTool().Execute(_db,
                            GetFlag("--from") ?? throw new ArgumentException("index-memory needs --from <memory dir>")));
                        break;
                    case "mem":
                        stdout.WriteLine(new MemTool(new UsageSignal()).ExecuteCli(_db, string.Join(' ', Positionals().Skip(1)), a.Contains("--hard")));
                        break;
                    case "brain":
                        BrainCmd([.. Positionals().Skip(1)]);
                        break;
                    case "shed-doc":
                        stdout.WriteLine(new ShedDocTool().Execute(_db, GetFlag("--path") ?? throw new ArgumentException("shed-doc needs --path <substring>")));
                        break;
                    case "shed-synthesis":
                        stdout.WriteLine(new ShedSynthesisTool().Execute(_db, GetFlag("--path") ?? throw new ArgumentException("shed-synthesis needs --path <source dir>")));
                        break;
                    case "add-synthesis":
                        stdout.WriteLine(new AddSynthesisTool().Execute(_db,
                            GetFlag("--path") ?? throw new ArgumentException("add-synthesis needs --path <source dir>"),
                            GetFlag("--from") ?? throw new ArgumentException("add-synthesis needs --from <file>"),
                            GetFlag("--title") ?? "",
                            GetFlag("--sources") ?? ""));
                        break;
                    case "shed-memory":
                        stdout.WriteLine(new ShedMemoryTool().ExecuteCli(_db, GetFlag("--key") ?? throw new ArgumentException("shed-memory needs --key <slug>")));
                        break;
                    case "shed-fact":
                        stdout.WriteLine(new ShedFactTool().Execute(_db, GetFlag("--key") ?? throw new ArgumentException("shed-fact needs --key <term>")));
                        break;
                    case "recompact-docs":
                        stdout.WriteLine(new RecompactDocsTool().Execute(_db));
                        break;
                    case "recall":
                        stdout.WriteLine(new RecallTool().ExecuteCli(_db, string.Join(' ', Positionals().Skip(1))));
                        break;
                    case "redact-chat":
                        stdout.WriteLine(new RedactChatTool().Execute(_db, _root, a.Contains("--dry-run")));
                        break;
                    case "eval":
                        stdout.WriteLine(new EvalTool().ExecuteCli(_db));
                        break;
                    case "add":
                    {
                        string term = GetFlag("--term") ?? throw new ArgumentException("add needs --term");
                        string prov = (GetFlag("--provenance") ?? "unverified").ToLowerInvariant();
                        stdout.WriteLine(new AddTool().Execute(_db, term, GetFlag("--aliases") ?? "[]", GetFlag("--category") ?? "manual",
                            GetFlag("--value") ?? "", GetFlag("--source") ?? "", GetFlag("--notes") ?? "", prov, GetFlag("--why") ?? "manual"));
                        break;
                    }
                    case "history":
                        stdout.WriteLine(new HistoryTool().ExecuteCli(_db, string.Join(' ', Positionals().Skip(1))));
                        break;
                    case "seed-edges":
                    {
                        string seedPath = GetFlag("--from") ?? PluginFileLocator.SeedPath();
                        // SeedEdgesTool.Execute returns the same "no spine file at ..." text the old inline SeedEdges
                        // wrote to stderr, so the missing-file case has to be checked here to keep it on stderr.
                        if (!File.Exists(seedPath)) { stderr.WriteLine($"no spine file at {Path.GetFullPath(seedPath)}"); break; }
                        stdout.WriteLine(new SeedEdgesTool().Execute(_db, seedPath));
                        break;
                    }
                    case "spine-export":
                        stdout.WriteLine(new SpineExportTool().ExecuteCli(_db,
                            GetFlag("--to") ?? PluginFileLocator.SeedPath()));
                        break;
                    // Same gap forget-project had: the graph could gain a node but never lose one, so anything indexed
                    // by mistake or moved out of scope stayed live forever. Retires on the timeline rather than
                    // deleting, because "indexed once, now out of scope" is a different fact from "never existed".
                    case "shed-node":
                    {
                        string nk = GetFlag("--key") ?? throw new ArgumentException("shed-node needs --key");
                        stdout.WriteLine(new ShedNodeTool().Execute(_db, _root, nk));
                        break;
                    }
                    case "spine-import":
                    {
                        string spineImportFrom = GetFlag("--from") ?? throw new ArgumentException("spine-import needs --from <spine.json>");
                        // SpineImportTool.ExecuteCli returns the same "no spine file at ..." text the old inline
                        // SpineImport wrote to stderr, so the missing-file case has to be checked here to keep it on
                        // stderr (same trap as seed-edges above).
                        if (!File.Exists(spineImportFrom)) { stderr.WriteLine($"no spine file at {spineImportFrom}"); break; }
                        stdout.WriteLine(new SpineImportTool().ExecuteCli(_db, spineImportFrom, stderr));
                        break;
                    }
                    case "project":
                        stdout.WriteLine(new ProjectTool().Execute(_db,
                            GetFlag("--name") ?? throw new ArgumentException("project needs --name"),
                            GetFlag("--root") ?? throw new ArgumentException("project needs --root"),
                            GetFlag("--lang") ?? "", GetFlag("--globs") ?? "*.ts,*.tsx,*.vue,*.kt,*.cs"));
                        break;
                    case "projects":
                        stdout.WriteLine(new ProjectsTool().Execute(_db));
                        break;
                    // Registration without deregistration left roots that no longer exist on disk still answering
                    // questions with paths that are gone, which is worse than not knowing.
                    case "forget-project":
                        stdout.WriteLine(new ForgetProjectTool().Execute(_db, _root,
                            GetFlag("--name") ?? throw new ArgumentException("forget-project needs --name")));
                        break;
                    case "extract-edges":
                        stdout.WriteLine(new ExtractEdgesTool().Execute(_db,
                            GetFlag("--symbol") ?? throw new ArgumentException("extract-edges needs --symbol"), GetFlag("--contract") ?? ""));
                        break;
                    case "candidates":
                        stdout.WriteLine(new CandidatesTool().Execute(_db, GetFlag("--symbol")));
                        break;
                    case "promote":
                        stdout.WriteLine(new PromoteTool().Execute(_db, int.Parse(Pos1(), CultureInfo.InvariantCulture)));
                        break;
                    case "promote-all":
                        stdout.WriteLine(new PromoteAllTool().Execute(_db, _root,
                            GetFlag("--symbol") ?? throw new ArgumentException("promote-all needs --symbol")));
                        break;
                    case "impact":
                        stdout.WriteLine(new ImpactTool().ExecuteCli(_db, string.Join(' ', Positionals().Skip(1))));
                        break;
                    case "graph-query":
                        stdout.WriteLine(new GraphQueryTool().ExecuteCli(_db, string.Join(' ', Positionals().Skip(1))));
                        break;
                    case "graph-path":
                    {
                        List<string> gp = [.. Positionals().Skip(1)];
                        if (gp.Count < 2) { stdout.WriteLine("usage: grimora graph-path <A> <B>"); break; }
                        stdout.WriteLine(new GraphPathTool().ExecuteCli(_db, gp[0], gp[1]));
                        break;
                    }
                    case "graph-explain":
                        stdout.WriteLine(new GraphExplainTool().ExecuteCli(_db, Pos1()));
                        break;
                    case "todo":
                        stdout.WriteLine(new TodoTool().Execute(_db, GetFlag("--title") ?? Pos1(), GetFlag("--why") ?? ""));
                        break;
                    case "todos":
                        stdout.WriteLine(new TodosTool().Execute(_db));
                        break;
                    case "done":
                        stdout.WriteLine(new DoneTool().Execute(_db, int.Parse(Pos1(), CultureInfo.InvariantCulture)));
                        break;
                    case "finding":
                        stdout.WriteLine(new FindingTool().ExecuteCli(_db, GetFlag("--title") ?? Pos1(), GetFlag("--detail") ?? "", GetFlag("--source") ?? ""));
                        break;
                    case "findings":
                        stdout.WriteLine(new FindingsTool().ExecuteCli(_db));
                        break;
                    case "resolve":
                        stdout.WriteLine(new ResolveTool().Execute(_db, int.Parse(Pos1(), CultureInfo.InvariantCulture)));
                        break;
                    case "stats":
                        stdout.WriteLine(a.Contains("--tokens")
                            ? new StatsTool().ExecuteTokens(_db, _instance)
                            : new StatsTool().Execute(_db, _instance, _dbPath));
                        break;
                    case "backup":
                        stdout.WriteLine(new BackupTool().Execute(_db, _root, GetFlag("--to")));
                        break;
                    case "loop":
                        // Removed in phase 2 (RESTRUCTURE.md section 2.1, drop 4 of 4): loop only ever wrote
                        // loop-state.json for loop-guard.mjs and session-continue.mjs, both dropped in the same
                        // pass; the NoMercy task record's stop-check does this job now. start/tick/clear existed
                        // only as loop's own sub-verbs, so this one message covers all three call shapes.
                        stderr.WriteLine("error: removed in 0.4: grimora loop is gone; the NoMercy task record replaces it.\n");
                        return 2;
                    case "stage":
                        stdout.WriteLine(new BrainStageTool().ExecuteCli(_db, Positionals().Skip(1).ToList(),
                            GetFlag("--gloss") ?? "", GetFlag("--scheme") ?? "", GetFlag("--facet") ?? "text",
                            GetFlag("--because") ?? "", a.Contains("--hard"), a.Contains("--multi")));
                        break;
                    case "flush":
                        stdout.WriteLine(new BrainFlushTool().ExecuteCli(_db));
                        break;
                    case "selftest":
                        // Removed in phase 2 (RESTRUCTURE.md section 2.1, drop 3 of 4): all 63 checks now have a C#
                        // test-project equivalent (SelfTestCoverageTests), so the inline TDD harness is gone.
                        stderr.WriteLine("error: removed in 0.4: grimora selftest is gone; its 63 checks now live in the test projects.\n");
                        return 2;
                    default:
                        // One command per line so adding/removing a command is a one-line diff, not a rewrite of the whole string.
                        string[] usage =
                        [
                            "init                                create/open the instance",
                            "import --from <db>                  merge another grimora.db into this one",
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
                        ];
                        string usageText = "grimora <command> [--instance <name>]\n\n" + string.Join("\n", usage);
                        if (cmd is "help" or "--help" or "-h")
                        {
                            stdout.WriteLine(usageText);
                            break;
                        }
                        // A caller that gets the help text back with exit 0 reads it as success while nothing ran.
                        stderr.WriteLine(cmd.StartsWith("-")
                            ? $"error: unknown command '{cmd}': the command comes first, flags go after the command.\n"
                            : $"error: unknown command '{cmd}'.\n");
                        stderr.WriteLine(usageText);
                        return 2;
                }
            }
            catch (ArgumentException argEx)
            {
                stderr.WriteLine($"error: {argEx.Message}");
                return 2;
            }
            catch (FormatException)
            {
                stderr.WriteLine("error: expected a numeric id (e.g. grimora done 3).");
                return 2;
            }
            return 0;
        }

        // One parsing rule with /cli (CliDispatch.FlagValue); the GetFlag("--x") call shape stays because
        // the flag-audit guard reads the verbs' flags from it.
        private string? GetFlag(string name) => FlagValue(a, name);

        // Generic instance resolution (explicit --instance already won at the call site): GRIMORA_INSTANCE,
        // else the project dir (CLAUDE_PROJECT_DIR or cwd) basename — the same binary serves any repo, no config.
        private string ResolveInstance()
        {
            string? env = Environment.GetEnvironmentVariable("GRIMORA_INSTANCE");
            if (!string.IsNullOrWhiteSpace(env)) return Slug(env);
            string? proj = Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR");
            string dir = string.IsNullOrWhiteSpace(proj) ? cwd : proj;
            string name = Path.GetFileName(dir.TrimEnd('/', '\\'));
            return string.IsNullOrWhiteSpace(name) ? "default" : Slug(name);
        }

        private string Slug(string text) => new([.. text.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')]);

        // Positional args only — drops every "--flag" AND the value following it (so a flag value never leaks into query terms).
        private List<string> Positionals()
        {
            List<string> p = [];
            for (int i = 0; i < a.Length; i++)
            {
                // A flag only consumes the next token as its value when that token isn't itself a flag — so a boolean
                // flag (--hard, --multi) followed by another flag or at the end doesn't swallow an unrelated positional.
                if (a[i].StartsWith("--")) { if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) i++; continue; }
                p.Add(a[i]);
            }
            return p;
        }

        private void Exec(string sql)
        {
            using SqliteCommand c = _db.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        // Run a statement that is allowed to fail benignly (idempotent migrations like ADD COLUMN on an existing column).
        private void TryExec(string sql)
        {
            try { Exec(sql); }
            catch (SqliteException) { }
        }

        private void Run(string sql, params (string name, object? val)[] ps)
        {
            using SqliteCommand c = _db.CreateCommand();
            c.CommandText = sql;
            foreach ((string name, object? val) in ps) c.Parameters.AddWithValue(name, val ?? DBNull.Value);
            c.ExecuteNonQuery();
        }

        private void Init()
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

        private void LogGap(string tool, string query)
        {
            string norm = string.Join(' ', Tokens(query));
            if (norm.Length < 3) return;
            Run(@"INSERT INTO gaps(query,tool,misses,first_ts,last_ts) VALUES($q,$t,1,$ts,$ts)
                ON CONFLICT(query) DO UPDATE SET misses=misses+1, last_ts=$ts, status='open', tool=$t",
                ("$q", norm), ("$t", tool), ("$ts", Now()));
        }

        // BRAIN v2 — a structured copy of the operator's mental model: a uniform (s,p,o) triple graph over typed
        // nodes, with a placement/convention slot overlay and a ref bridge into the existing channels. Bi-temporal
        // (supersede, never delete). Predicate vocabulary + link-object existence are enforced by TRIGGERS (which
        // fire regardless of the per-connection foreign_keys pragma) so a typo'd relation is a write error, not a
        // silently broken graph. Designed + SQL-verified by the grimora-brain-schema workflow. Runs after Init() —
        // the ref trigger and legacy_consumes view reference the base channels Init() creates.
        private void InitBrain()
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

        private string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        private string Pos1() => Positionals().Skip(1).FirstOrDefault() ?? "";

        private long ScalarLong(string sql, params (string name, object? val)[] ps)
        {
            using SqliteCommand c = _db.CreateCommand();
            c.CommandText = sql;
            foreach ((string name, object? val) in ps) c.Parameters.AddWithValue(name, val ?? DBNull.Value);
            return (long)(c.ExecuteScalar() ?? 0L);
        }

        private string? ScalarText(string sql, params (string name, object? val)[] ps)
        {
            using SqliteCommand c = _db.CreateCommand();
            c.CommandText = sql;
            foreach ((string name, object? val) in ps) c.Parameters.AddWithValue(name, val ?? DBNull.Value);
            object? result = c.ExecuteScalar();
            return result is null or DBNull ? null : (string)result;
        }

        // Split on every non-alphanumeric, not just space. Stripping the punctuation out of a whole word glued
        // "nomercy-app-kmp" into "nomercyappkmp" and "the-effortless-encoder" into one nonsense token, neither
        // of which exists in the index — FTS5's own tokenizer breaks on those same characters, so any query
        // written the way a repo, package, or folder is actually named matched nothing at all.
        private List<string> Tokens(string terms) =>
            [.. terms.ToLowerInvariant()
                .Split(" \t\r\n-_./\\,;:()[]{}<>\"'`|!?*+=&#@~%$^".ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.All(char.IsLetterOrDigit) && t.Length > 1 && !_stop.Contains(t))
                .Distinct()];

        // Store an answer that was distilled from a set of source files.
        //
        // The compacted sources are barely smaller than the originals — prose has no markdown chrome to strip —
        // so serving them back saves nothing. What is worth keeping is the ANSWER: a session read twelve files
        // totalling 140 KB to write a 15 KB orientation brief, threw the brief away, and left the next person to
        // read the same 140 KB. A synthesis is that brief, filed against the directory it came from.
        //
        // It is explicitly second-hand: category 'synthesis', sources listed, and stamped with the newest source
        // mtime so a reader can tell when the sources have moved on underneath it.

        // BRAIN router — one-shot recall over the knowledge graph. Each read is a single self-contained statement
        // against the *_now (live-only) views; no nested calls, no query-time code grep.
        private void BrainCmd(List<string> rest)
        {
            string sub = rest.Count > 0 ? rest[0] : "help";
            List<string> rargs = [.. rest.Skip(1)];
            switch (sub)
            {
                case "core": stdout.WriteLine(new BrainCoreTool().ExecuteCli(_db)); break;
                case "scope": stdout.WriteLine(new BrainScopeTool().ExecuteCli(_db, rargs)); break;
                case "common": stdout.WriteLine(new BrainCommonTool().ExecuteCli(_db, rargs)); break;
                case "place": stdout.WriteLine(new BrainPlaceTool().ExecuteCli(_db, rargs.FirstOrDefault() ?? "")); break;
                case "recall": BrainRecall(string.Join(' ', rargs)); break;
                case "impact": BrainImpact(string.Join(' ', rargs)); break;
                case "learn":
                {
                    string learnResult = new BrainLearnTool().ExecuteCli(_db, rargs, GetFlag("--gloss") ?? "",
                        GetFlag("--scheme") ?? "", GetFlag("--facet") ?? "text", GetFlag("--because") ?? "", a.Contains("--hard"), a.Contains("--multi"));
                    if (learnResult.Length > 0) stdout.WriteLine(learnResult);
                    break;
                }
                case "learn-batch":
                    stdout.WriteLine(new BrainLearnBatchTool().ExecuteCli(_db,
                        GetFlag("--from") ?? throw new ArgumentException("brain learn-batch needs --from <file>"), stderr));
                    break;
                case "set-hard":
                    if (rargs.Count < 2) { stdout.WriteLine("usage: brain set-hard <node-key> <0|1>"); break; }
                    stdout.WriteLine(new BrainSetHardTool().Execute(_db, rargs[0], rargs[1] == "1"));
                    break;
                case "export": stdout.WriteLine(new BrainExportTool().ExecuteCli(_db, GetFlag("--to") ?? Path.Combine(_root, "brain-export.txt"))); break;
                case "audit": stdout.WriteLine(new BrainAuditTool().Execute(_db, _instance)); break;
                case "tidy": stdout.WriteLine(new BrainTidyTool().Execute(_db, _root)); break;
                case "why":
                    stdout.WriteLine(rargs.Count < 1 ? "usage: brain why <node-key>" : new BrainWhyTool().Execute(_db, rargs[0]));
                    break;
                case "merge":
                    if (rargs.Count < 2) { stdout.WriteLine("usage: brain merge <from-key> <into-key>"); break; }
                    stdout.WriteLine(new BrainMergeTool().Execute(_db, _root, rargs[0], rargs[1]));
                    break;
                case "verify":
                    stdout.WriteLine(rargs.Count < 1 ? "usage: brain verify <node-key>" : new BrainVerifyTool().Execute(_db, rargs[0]));
                    break;
                case "forget":
                    if (rargs.Count < 1) { stdout.WriteLine("usage: brain forget <node-key>"); break; }
                    stdout.WriteLine(new BrainForgetTool().Execute(_db, _root, rargs[0]));
                    break;
                case "unlink":
                    if (rargs.Count < 3) { stdout.WriteLine("usage: brain unlink <subject> <predicate> <object>"); break; }
                    stdout.WriteLine(new BrainUnlinkTool().Execute(_db, _root, rargs[0], rargs[1], rargs[2]));
                    break;
                case "stale": stdout.WriteLine(new BrainStaleTool().Execute(_db, int.TryParse(GetFlag("--days"), out int sd) ? sd : 30)); break;
                case "distill": stdout.WriteLine(new BrainDistillTool().Execute(_db, _root)); break;
                case "seed":
                {
                    string brainSeedFrom = GetFlag("--from") ?? PluginFileLocator.SeedPath();
                    // BrainSeedTool.ExecuteCli returns the same "no spine file at ..." text the old inline
                    // BrainSeed wrote to stderr, so the missing-file case has to be checked here to keep it on
                    // stderr (same trap as seed-edges/spine-import above).
                    if (!File.Exists(brainSeedFrom))
                    {
                        stderr.WriteLine($"no spine file at {Path.GetFullPath(brainSeedFrom)} — run `grimora spine-export` on an instance that already has one, or write the file by hand (see README).");
                        break;
                    }
                    stdout.WriteLine(new BrainSeedTool().ExecuteCli(_db, brainSeedFrom));
                    break;
                }
                case "stats": BrainStats(); break;
                case "gaps": stdout.WriteLine(new BrainGapsTool().ExecuteCli(_db)); break;
                default:
                    stdout.WriteLine("brain <core|scope <proj…>|common <proj…>|place <codekind>|recall <text>|impact <symbol>|learn …|gaps|distill|stats>");
                    break;
            }
        }

        // Execute, print, and return each row's first column (the node key for a brain read). The reader is disposed
        // before the caller reinforces, so the follow-up write never races an open reader on the single connection.
        private List<string> RunReader(SqliteCommand c, bool announceEmpty = true)
        {
            List<string> keys = [];
            int n = 0;
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read())
            {
                List<string> cols = [];
                for (int i = 0; i < r.FieldCount; i++)
                    cols.Add(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "");
                if (cols.Count > 0) keys.Add(cols[0]);
                stdout.WriteLine("  " + string.Join("  |  ", cols));
                n++;
            }
            if (n == 0 && announceEmpty) stdout.WriteLine("  (nothing)");
            return keys;
        }

        // Reinforce surfaced nodes: bump hits + recency. Guarded on live-node existence so non-node first columns
        // (slot names, project shorthands) never create junk usage rows. This is the automatic half of "smarter with use".
        private void Reinforce(IEnumerable<string> keys)
        {
            foreach (string k in keys.Where(s => s.Length > 0).Distinct())
                Run("INSERT INTO usage(node_k,hits,last_used) SELECT $k,1,$ts WHERE EXISTS(SELECT 1 FROM node_now WHERE k=$k) " +
                    "ON CONFLICT(node_k) DO UPDATE SET hits=hits+1, last_used=$ts",
                    ("$k", k), ("$ts", Now()));
        }

        // Free-text recall — DB-side alias expansion -> porter FTS5 seed (live-filtered) -> same-statement traversal
        // to who-uses-it + ref-pulled fact/rule. One natural-language question, one query.
        private void BrainRecall(string text)
        {
            if (text.Trim().Length == 0) { stdout.WriteLine("usage: brain recall <free text>"); return; }
            List<string> toks = Tokens(text);
            if (toks.Count == 0) { stdout.WriteLine("  (no usable terms)"); return; }
            string rawList = string.Join(",", toks.Select((_, i) => $"($q{i})"));
            using SqliteCommand c = _db.CreateCommand();
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
                using SqliteCommand fb = _db.CreateCommand();
                string likeClauses = string.Join(" OR ", toks.Select((_, i) => $"label LIKE $l{i} OR gloss LIKE $l{i}"));
                fb.CommandText = $"SELECT k, kind, label, gloss FROM node_now WHERE {likeClauses} LIMIT 8";
                for (int i = 0; i < toks.Count; i++) fb.Parameters.AddWithValue($"$l{i}", "%" + toks[i] + "%");
                stdout.WriteLine("  (no FTS match — substring fallback)");
                List<string> fbHits = RunReader(fb);
                Reinforce(fbHits);
                if (fbHits.Count == 0) LogGap("brain_recall", text);
            }
        }

        // Cross-project impact — every consumer of a shared symbol/contract from the still-live, alias-corrected
        // edges view plus the summary triple. Call before changing a shared contract.
        private void BrainImpact(string term)
        {
            if (term.Trim().Length == 0) { stdout.WriteLine("usage: brain impact <symbol-or-contract>"); return; }
            using SqliteCommand c = _db.CreateCommand();
            c.CommandText = @"SELECT s AS project, o AS contract_symbol, file, line, hardcoded
                FROM legacy_consumes WHERE o LIKE $like
                UNION ALL
                SELECT substr(t.s,6), t.o, NULL, NULL, NULL
                FROM triple_now t
                WHERE t.p='consumes' AND t.o LIKE $like AND t.o_is_literal=0";
            c.Parameters.AddWithValue("$like", "%" + term + "%");
            Reinforce(RunReader(c));
        }

        private void BrainStats()
        {
            stdout.WriteLine($"  nodes   {ScalarLong("SELECT count(*) FROM node_now")}  ({ScalarLong("SELECT count(*) FROM node_now WHERE hard=1")} hard)");
            stdout.WriteLine($"  triples {ScalarLong("SELECT count(*) FROM triple_now")}");
            stdout.WriteLine($"  slots   {ScalarLong("SELECT count(*) FROM slot_now")}");
            stdout.WriteLine($"  refs    {ScalarLong("SELECT count(*) FROM ref")}");
            stdout.WriteLine($"  usage   {ScalarLong("SELECT count(*) FROM usage")} reinforced; top: {ScalarText("SELECT group_concat(node_k||'×'||hits,', ') FROM (SELECT node_k,hits FROM usage ORDER BY hits DESC LIMIT 3)") ?? "(none yet)"}");
            stdout.WriteLine($"  coverage  seams {ScalarLong("SELECT count(*) FROM node_now n WHERE kind='seam' AND EXISTS(SELECT 1 FROM triple_now WHERE o=n.k AND p='consumes')")}/{ScalarLong("SELECT count(*) FROM node_now WHERE kind='seam'")} consumed, codekinds {ScalarLong("SELECT count(*) FROM node_now n WHERE kind='codekind' AND EXISTS(SELECT 1 FROM slot_now WHERE frame_k=n.k)")}/{ScalarLong("SELECT count(*) FROM node_now WHERE kind='codekind'")} placed");
            stdout.WriteLine($"  fresh   {ScalarLong("SELECT count(*) FROM node_now n JOIN usage u ON u.node_k=n.k WHERE u.verified_at IS NOT NULL")}/{ScalarLong("SELECT count(*) FROM node_now")} confirmed against code");
            stdout.WriteLine($"  gaps    {ScalarLong("SELECT count(*) FROM gaps WHERE status='open'")} open / {ScalarLong("SELECT count(*) FROM gaps")} total (unanswerable lookups awaiting a learn)");
            using SqliteCommand c = _db.CreateCommand();
            c.CommandText = "SELECT kind, count(*) FROM node_now GROUP BY kind ORDER BY 2 DESC";
            using SqliteDataReader r = c.ExecuteReader();
            while (r.Read()) stdout.WriteLine($"    {r.GetString(0),-12} {r.GetInt32(1)}");
        }
    }
}
