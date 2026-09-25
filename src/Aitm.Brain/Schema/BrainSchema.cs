using Aitm.Store.Schema;

namespace Aitm.Brain.Schema;

/// <summary>
/// Brain's 8 tables (RESTRUCTURE.md section 3.1): <c>node</c>, <c>triple</c>, <c>slot</c>, <c>ref</c>,
/// <c>term_alias</c>, <c>proj_alias</c>, <c>pred_vocab</c>, <c>distill_log</c> (plus <c>node_fts</c>,
/// <c>slot_fts</c>, and the 4 views <c>node_now</c>, <c>triple_now</c>, <c>slot_now</c>,
/// <c>legacy_consumes</c>). Every statement is copied verbatim, in the same order, from today's aitm.cs
/// InitBrain() — including its own cross-table order (the FTS sync triggers and views come after the
/// base tables they read/mirror; the child tables' FK-checking triggers come after node/triple/slot exist)
/// — except the final <c>CREATE TABLE IF NOT EXISTS usage(...)</c> statement (aitm.cs:585) and its
/// follow-up <c>TryExec("ALTER TABLE usage ADD COLUMN verified_at")</c> (aitm.cs:587): <c>usage</c> is
/// Store's table (section 3.1), and <see cref="Store.Schema.StoreSchema"/> already creates it, with the
/// <c>verified_at</c> column folded in, from slice 3a.
/// </summary>
public sealed class BrainSchema : ISchemaProvider
{
    public string Name => "Brain";

    public IReadOnlyList<string> Statements { get; } =
    [
        // aitm.cs:453-457
        """
        CREATE TABLE IF NOT EXISTS pred_vocab (
          p TEXT PRIMARY KEY, inverse TEXT, is_link INTEGER NOT NULL DEFAULT 1,
          transitive INTEGER NOT NULL DEFAULT 0, is_sharing INTEGER NOT NULL DEFAULT 0,
          conflicts TEXT, note TEXT
        );
        """,
        // aitm.cs:458-469
        """
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
        """,
        // aitm.cs:471-476
        """
        CREATE TABLE IF NOT EXISTS node (
          id INTEGER PRIMARY KEY, k TEXT NOT NULL, kind TEXT NOT NULL, label TEXT NOT NULL,
          gloss TEXT DEFAULT '', scheme TEXT, props TEXT NOT NULL DEFAULT '{}', hard INTEGER NOT NULL DEFAULT 0,
          valid_from TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), valid_to TEXT,
          superseded_by INTEGER REFERENCES node(id)
        );
        """,
        // aitm.cs:477-480
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_node_live ON node(k) WHERE valid_to IS NULL;",
        "CREATE INDEX IF NOT EXISTS ix_node_kind ON node(kind) WHERE valid_to IS NULL;",
        "CREATE INDEX IF NOT EXISTS ix_node_scheme ON node(scheme) WHERE valid_to IS NULL;",
        "CREATE INDEX IF NOT EXISTS ix_node_hard ON node(hard) WHERE valid_to IS NULL AND hard = 1;",
        // aitm.cs:482-488
        """
        CREATE TABLE IF NOT EXISTS triple (
          id INTEGER PRIMARY KEY, s TEXT NOT NULL, p TEXT NOT NULL REFERENCES pred_vocab(p), o TEXT NOT NULL,
          o_is_literal INTEGER NOT NULL DEFAULT 0, ov TEXT, because TEXT DEFAULT '', conf REAL NOT NULL DEFAULT 1.0,
          hard INTEGER NOT NULL DEFAULT 0, src TEXT,
          valid_from TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), valid_to TEXT,
          superseded_by INTEGER REFERENCES triple(id)
        );
        """,
        // aitm.cs:489-493
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_triple_live ON triple(s,p,o) WHERE valid_to IS NULL;",
        "CREATE INDEX IF NOT EXISTS ix_triple_spo ON triple(s,p,o) WHERE valid_to IS NULL;",
        "CREATE INDEX IF NOT EXISTS ix_triple_pos ON triple(p,o,s) WHERE valid_to IS NULL;",
        "CREATE INDEX IF NOT EXISTS ix_triple_osp ON triple(o,s) WHERE valid_to IS NULL AND o_is_literal=0;",
        "CREATE INDEX IF NOT EXISTS ix_triple_hard ON triple(s,p) WHERE valid_to IS NULL AND hard=1;",
        // aitm.cs:495-508
        "CREATE TRIGGER IF NOT EXISTS triple_pred_guard_ins BEFORE INSERT ON triple\n" +
        "WHEN NEW.p NOT IN (SELECT p FROM pred_vocab) BEGIN SELECT RAISE(ABORT,'unknown predicate'); END;",
        "CREATE TRIGGER IF NOT EXISTS triple_pred_guard_upd BEFORE UPDATE OF p ON triple\n" +
        "WHEN NEW.p NOT IN (SELECT p FROM pred_vocab) BEGIN SELECT RAISE(ABORT,'unknown predicate'); END;",
        "CREATE TRIGGER IF NOT EXISTS triple_litflag_ins BEFORE INSERT ON triple\n" +
        "WHEN NEW.o_is_literal != (SELECT 1 - is_link FROM pred_vocab WHERE p = NEW.p) BEGIN SELECT RAISE(ABORT,'o_is_literal must match pred_vocab.is_link'); END;",
        "CREATE TRIGGER IF NOT EXISTS triple_litflag_upd BEFORE UPDATE OF o,o_is_literal,p ON triple\n" +
        "WHEN NEW.o_is_literal != (SELECT 1 - is_link FROM pred_vocab WHERE p = NEW.p) BEGIN SELECT RAISE(ABORT,'o_is_literal must match pred_vocab.is_link'); END;",
        "CREATE TRIGGER IF NOT EXISTS triple_obj_fk_ins BEFORE INSERT ON triple\n" +
        "WHEN NEW.o_is_literal = 0 AND NOT EXISTS(SELECT 1 FROM node WHERE k = NEW.o AND valid_to IS NULL) BEGIN SELECT RAISE(ABORT,'dangling link object'); END;",
        "CREATE TRIGGER IF NOT EXISTS triple_obj_fk_upd BEFORE UPDATE OF o,o_is_literal ON triple\n" +
        "WHEN NEW.o_is_literal = 0 AND NOT EXISTS(SELECT 1 FROM node WHERE k = NEW.o AND valid_to IS NULL) BEGIN SELECT RAISE(ABORT,'dangling link object'); END;",
        "CREATE TRIGGER IF NOT EXISTS triple_subj_fk_ins BEFORE INSERT ON triple\n" +
        "WHEN NOT EXISTS(SELECT 1 FROM node WHERE k = NEW.s AND valid_to IS NULL) BEGIN SELECT RAISE(ABORT,'dangling subject'); END;",
        // aitm.cs:510-515
        """
        CREATE TABLE IF NOT EXISTS slot (
          id INTEGER PRIMARY KEY, frame_k TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL,
          facet TEXT DEFAULT 'text', multi INTEGER NOT NULL DEFAULT 0, because TEXT DEFAULT '', src TEXT DEFAULT '',
          valid_from TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), valid_to TEXT,
          superseded_by INTEGER REFERENCES slot(id)
        );
        """,
        // aitm.cs:516-518
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_slot_single ON slot(frame_k,name) WHERE valid_to IS NULL AND multi = 0;",
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_slot_multi ON slot(frame_k,name,value) WHERE valid_to IS NULL AND multi = 1;",
        "CREATE INDEX IF NOT EXISTS ix_slot_name ON slot(name) WHERE valid_to IS NULL;",
        // aitm.cs:519-520
        "CREATE TRIGGER IF NOT EXISTS slot_frame_fk_ins BEFORE INSERT ON slot\n" +
        "WHEN NOT EXISTS(SELECT 1 FROM node WHERE k = NEW.frame_k AND valid_to IS NULL) BEGIN SELECT RAISE(ABORT,'dangling slot frame'); END;",
        // aitm.cs:522-525
        """
        CREATE TABLE IF NOT EXISTS ref (
          node_k TEXT NOT NULL, channel TEXT NOT NULL CHECK (channel IN ('facts','memory','docs','chat','edges')),
          payload_k TEXT NOT NULL, role TEXT DEFAULT '', PRIMARY KEY (node_k, channel, payload_k)
        );
        """,
        // aitm.cs:526-527
        "CREATE INDEX IF NOT EXISTS ix_ref_payload ON ref(channel, payload_k);",
        "CREATE INDEX IF NOT EXISTS ix_ref_node ON ref(node_k);",
        // aitm.cs:528-537
        """
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
        """,
        // aitm.cs:539
        "CREATE TABLE IF NOT EXISTS term_alias (term TEXT NOT NULL, canonical TEXT NOT NULL, PRIMARY KEY (term, canonical));",
        // aitm.cs:543-546
        """
        INSERT OR IGNORE INTO term_alias(term,canonical) VALUES
         ('authentication','auth'),('signin','auth'),('login','auth'),
         ('localization','i18n'),('translation','i18n'),('accessibility','a11y'),
         ('endpoint','api'),('rest','api'),('controller','api'),('dto','contract'),('placement','belongs_in');
        """,
        // aitm.cs:548
        "CREATE VIRTUAL TABLE IF NOT EXISTS node_fts USING fts5(k UNINDEXED, label, gloss, props, content='node', content_rowid='id', tokenize='porter unicode61');",
        // aitm.cs:549-554
        "CREATE TRIGGER IF NOT EXISTS node_ai AFTER INSERT ON node BEGIN INSERT INTO node_fts(rowid,k,label,gloss,props) VALUES(new.id,new.k,new.label,new.gloss,new.props); END;",
        "CREATE TRIGGER IF NOT EXISTS node_ad AFTER DELETE ON node BEGIN INSERT INTO node_fts(node_fts,rowid,k,label,gloss,props) VALUES('delete',old.id,old.k,old.label,old.gloss,old.props); END;",
        """
        CREATE TRIGGER IF NOT EXISTS node_au AFTER UPDATE ON node BEGIN
          INSERT INTO node_fts(node_fts,rowid,k,label,gloss,props) VALUES('delete',old.id,old.k,old.label,old.gloss,old.props);
          INSERT INTO node_fts(rowid,k,label,gloss,props) VALUES(new.id,new.k,new.label,new.gloss,new.props);
        END;
        """,
        // aitm.cs:556
        "CREATE VIRTUAL TABLE IF NOT EXISTS slot_fts USING fts5(frame_k UNINDEXED, name, value, because, content='slot', content_rowid='id', tokenize='porter unicode61');",
        // aitm.cs:557-562
        "CREATE TRIGGER IF NOT EXISTS slot_ai AFTER INSERT ON slot BEGIN INSERT INTO slot_fts(rowid,frame_k,name,value,because) VALUES(new.id,new.frame_k,new.name,new.value,new.because); END;",
        "CREATE TRIGGER IF NOT EXISTS slot_ad AFTER DELETE ON slot BEGIN INSERT INTO slot_fts(slot_fts,rowid,frame_k,name,value,because) VALUES('delete',old.id,old.frame_k,old.name,old.value,old.because); END;",
        """
        CREATE TRIGGER IF NOT EXISTS slot_au AFTER UPDATE ON slot BEGIN
          INSERT INTO slot_fts(slot_fts,rowid,frame_k,name,value,because) VALUES('delete',old.id,old.frame_k,old.name,old.value,old.because);
          INSERT INTO slot_fts(rowid,frame_k,name,value,because) VALUES(new.id,new.frame_k,new.name,new.value,new.because);
        END;
        """,
        // aitm.cs:564-566
        "CREATE VIEW IF NOT EXISTS node_now AS SELECT * FROM node WHERE valid_to IS NULL;",
        "CREATE VIEW IF NOT EXISTS triple_now AS SELECT * FROM triple WHERE valid_to IS NULL;",
        "CREATE VIEW IF NOT EXISTS slot_now AS SELECT * FROM slot WHERE valid_to IS NULL;",
        // aitm.cs:570
        "CREATE TABLE IF NOT EXISTS proj_alias (short TEXT PRIMARY KEY, k TEXT NOT NULL);",
        // aitm.cs:571-574
        """
        CREATE VIEW IF NOT EXISTS legacy_consumes AS
          SELECT COALESCE(a.k,'proj:'||e.project) AS s, 'consumes' AS p,
                 'contract:'||e.contract||'.'||e.symbol AS o, e.file, e.line, e.usage, e.hardcoded
          FROM edges e LEFT JOIN proj_alias a ON a.short = e.project;
        """,
        // aitm.cs:576-579
        """
        CREATE TABLE IF NOT EXISTS distill_log (
          node_k TEXT NOT NULL, source_file TEXT NOT NULL, content_hash TEXT NOT NULL,
          dropped TEXT DEFAULT '', unresolved TEXT DEFAULT '', PRIMARY KEY (node_k, source_file)
        );
        """,
        // aitm.cs:580
        "CREATE INDEX IF NOT EXISTS ix_distill_hash ON distill_log(content_hash);",
    ];
}
