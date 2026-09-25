using Aitm.Brain.Schema;
using Aitm.Docs.Schema;
using Aitm.Facts.Schema;
using Aitm.Graph.Schema;
using Aitm.Memory.Schema;
using Aitm.Store.Schema;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Store.Tests;

// RESTRUCTURE.md slice 3b: "the other tables' DDL ... moved into their owning projects." This pins the
// whole store: applying every schema provider (Store, plus the 5 new ones this slice adds), in the
// runner's dependency order, to an empty temp store must give sqlite_master rows identical to what
// today's aitm.cs Init() (aitm.cs:373) and InitBrain() (aitm.cs:450) create on an empty temp store —
// type, name, tbl_name, and whitespace-normalized sql, for every table, FTS shadow table, trigger,
// index and view. TodaysInit/TodaysInitBrain below are verbatim copies of aitm.cs's own statements
// (including its two TryExec-style migration ALTERs, which do fire on an empty store) — the oracle,
// not a reimplementation. edges_file_idx (aitm.cs:2562 / mcp.cs:1067) is intentionally excluded from
// both sides: it is created lazily by GraphBfs, never by Init()/InitBrain(), so neither the oracle nor
// the providers create it on an empty store (see the remarks on GraphSchema for the section-3.1
// conflict this leaves open for a later slice).
public class WholeStoreSchemaDdlParityTests
{
    private static readonly string[] TodaysInit =
    [
        // aitm.cs:375 (table) + :379 (TryExec ALTER — succeeds on an empty store)
        "CREATE TABLE IF NOT EXISTS facts(k TEXT PRIMARY KEY, term TEXT, aliases TEXT, category TEXT, value TEXT, source TEXT, notes TEXT);",
        "ALTER TABLE facts ADD COLUMN provenance TEXT NOT NULL DEFAULT 'unverified';",
        // aitm.cs:380
        "CREATE TABLE IF NOT EXISTS mutations(id INTEGER PRIMARY KEY, ts TEXT, op TEXT, kind TEXT, k TEXT, before TEXT, after TEXT, why TEXT);",
        // aitm.cs:381
        "CREATE VIRTUAL TABLE IF NOT EXISTS facts_fts USING fts5(k UNINDEXED, term, aliases, category, value, notes);",
        // aitm.cs:382
        "CREATE TABLE IF NOT EXISTS edges(id INTEGER PRIMARY KEY, symbol TEXT, contract TEXT, project TEXT, file TEXT, line INTEGER, usage TEXT, hardcoded INTEGER);",
        // aitm.cs:383
        "CREATE TABLE IF NOT EXISTS todos(id INTEGER PRIMARY KEY, ts TEXT, title TEXT, status TEXT, why TEXT);",
        // aitm.cs:384
        "CREATE TABLE IF NOT EXISTS findings(id INTEGER PRIMARY KEY, ts TEXT, title TEXT, detail TEXT, source TEXT, status TEXT);",
        // aitm.cs:386
        "CREATE TABLE IF NOT EXISTS chat(k TEXT PRIMARY KEY, session TEXT, ts TEXT, role TEXT, text TEXT);",
        // aitm.cs:387
        "CREATE VIRTUAL TABLE IF NOT EXISTS chat_fts USING fts5(k UNINDEXED, text);",
        // aitm.cs:390
        "CREATE TABLE IF NOT EXISTS projects(name TEXT PRIMARY KEY, root TEXT, lang TEXT, globs TEXT);",
        // aitm.cs:391
        "CREATE TABLE IF NOT EXISTS edge_candidates(id INTEGER PRIMARY KEY, symbol TEXT, contract TEXT, project TEXT, file TEXT, line INTEGER, usage TEXT, hardcoded INTEGER, status TEXT);",
        // aitm.cs:394 (table) + :395 (TryExec ALTER — fails, column already declared; caught, no-op)
        "CREATE TABLE IF NOT EXISTS docs(k TEXT PRIMARY KEY, path TEXT, title TEXT, category TEXT, content TEXT, terms TEXT);",
        // aitm.cs:396
        "CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts USING fts5(k UNINDEXED, title, content);",
        // aitm.cs:400
        "CREATE TABLE IF NOT EXISTS memory(k TEXT PRIMARY KEY, type TEXT, title TEXT, hook TEXT, body TEXT, links TEXT, hard INTEGER);",
        // aitm.cs:401
        "CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(k UNINDEXED, title, hook, body);",
        // aitm.cs:405
        "CREATE TABLE IF NOT EXISTS gaps(id INTEGER PRIMARY KEY, query TEXT NOT NULL UNIQUE, tool TEXT NOT NULL, misses INTEGER NOT NULL DEFAULT 1, first_ts TEXT NOT NULL, last_ts TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'open');",
        // aitm.cs:406
        "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);",
    ];

    // aitm.cs's TryExec("ALTER TABLE docs ADD COLUMN terms TEXT") fails on an empty store (the column
    // already sits in the CREATE above) and TryExec swallows the failure — mirrored here the same way.
    private static readonly HashSet<string> ExpectedToFailOnEmptyStore =
        new(StringComparer.Ordinal) { "ALTER TABLE docs ADD COLUMN terms TEXT" };

    // aitm.cs:452-586, verbatim (the one big multi-statement Exec() call), minus the final
    // "CREATE TABLE IF NOT EXISTS usage(...)" (aitm.cs:585): usage is Store's table, already created —
    // with its verified_at column folded in — by StoreSchema (slice 3a).
    private const string TodaysInitBrain = """
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
        """;

    [Fact]
    public void EveryProviderTogetherMatchesTodaysInitPlusInitBrain()
    {
        using SqliteConnection oldStore = OpenMemory();
        // aitm.cs's own StoreSchema (slice 3a) tables are the ones Init()/InitBrain() also create
        // (mutations, gaps, meta directly; usage via InitBrain()) — apply it first so the oracle's
        // "usage" table exists for parity, exactly as InitBrain() relies on Init() having already run.
        SchemaRunner.Apply(oldStore, [new StoreSchema()]);
        foreach (string statement in TodaysInit) ExecMaybeFailing(oldStore, statement);
        // aitm.cs runs the whole InitBrain() block as one Exec() call (one CommandText, many
        // statements) — mirrored here the same way, rather than split into a list.
        ExecMaybeFailing(oldStore, TodaysInitBrain);

        using SqliteConnection newStore = OpenMemory();
        SchemaRunner.Apply(newStore, [
            new StoreSchema(),
            new FactsSchema(),
            new MemorySchema(),
            new DocsSchema(),
            new GraphSchema(),
            new BrainSchema(),
        ]);

        List<(string type, string name, string tblName, string sql)> oldRows = SqliteMasterRows(oldStore);
        List<(string type, string name, string tblName, string sql)> newRows = SqliteMasterRows(newStore);

        Assert.NotEmpty(oldRows);
        Assert.Equal(oldRows, newRows);
    }

    private static SqliteConnection OpenMemory()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void ExecMaybeFailing(SqliteConnection connection, string sql)
    {
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        catch when (ExpectedToFailOnEmptyStore.Contains(sql.Trim()))
        {
            // mirrors aitm.cs's TryExec: swallow the expected "column already exists" failure.
        }
    }

    private static List<(string type, string name, string tblName, string sql)> SqliteMasterRows(SqliteConnection connection)
    {
        List<(string, string, string, string)> rows = new();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? sql = reader.IsDBNull(3) ? null : reader.GetString(3);
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), Normalize(sql)));
        }
        return rows;
    }

    private static string Normalize(string? sql) =>
        sql is null ? "" : string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
