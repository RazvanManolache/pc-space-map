using Microsoft.Data.Sqlite;
using PcSpaceMap.Models;
using System.Text.Json;

namespace PcSpaceMap.Services;

// Immutable snapshot intervals share unchanged metadata. Full paths are stored
// only for directories; files refer to an integer parent and a basename.
internal sealed partial class CompactInventory(InventoryStore owner)
{
    private readonly InventoryStore _owner = owner;
    private long _root;
    private bool _full;
    internal bool IsFullScan => _full;
    internal bool HasPrevious { get; private set; }
    private long _foldersVisited;
    private ReaderSelection _readers = new(1, false, false);
    internal void SetReaderSelection(ReaderSelection selection) => _readers = selection;
    internal void Initialize(SqliteConnection connection)
    {
        InventoryStore.Execute(connection, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS scans(id INTEGER PRIMARY KEY,root TEXT NOT NULL,completed TEXT,method TEXT NOT NULL DEFAULT '',
                journal TEXT,issues TEXT NOT NULL DEFAULT '[]',skipped INTEGER NOT NULL DEFAULT 0,suggestions TEXT,changes TEXT,timings TEXT,work TEXT);
            CREATE TABLE IF NOT EXISTS c_nodes(id INTEGER PRIMARY KEY,parent INTEGER NOT NULL,name TEXT COLLATE NOCASE NOT NULL,UNIQUE(parent,name));
            CREATE TABLE IF NOT EXISTS c_directories(node INTEGER PRIMARY KEY,path TEXT COLLATE NOCASE NOT NULL UNIQUE,depth INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS c_spelling(node INTEGER NOT NULL,first INTEGER NOT NULL,until INTEGER,name TEXT NOT NULL,path TEXT,
                PRIMARY KEY(node,first)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS c_spelling_active ON c_spelling(node) WHERE until IS NULL;
            CREATE TABLE IF NOT EXISTS c_versions(node INTEGER NOT NULL,first INTEGER NOT NULL,until INTEGER,parent INTEGER NOT NULL,
                dir INTEGER NOT NULL,depth INTEGER NOT NULL,size INTEGER NOT NULL,allocated INTEGER,identity TEXT,
                modified INTEGER NOT NULL,attributes INTEGER NOT NULL,files INTEGER NOT NULL,folders INTEGER NOT NULL,
                unique_allocated INTEGER,skipped INTEGER NOT NULL,mask INTEGER NOT NULL,PRIMARY KEY(node,first)) WITHOUT ROWID;
            CREATE UNIQUE INDEX IF NOT EXISTS cv_current ON c_versions(node) WHERE until IS NULL;
            CREATE INDEX IF NOT EXISTS cv_size ON c_versions(dir,size DESC,node) WHERE until IS NULL AND skipped=0;
            CREATE INDEX IF NOT EXISTS cv_parent ON c_versions(parent,size DESC,node) WHERE until IS NULL AND skipped=0;
            CREATE INDEX IF NOT EXISTS cv_skipped ON c_versions(node) WHERE until IS NULL AND skipped=1;
            CREATE INDEX IF NOT EXISTS cv_identity ON c_versions(identity,node) WHERE until IS NULL AND identity IS NOT NULL AND dir=0 AND skipped=0;
            CREATE INDEX IF NOT EXISTS cv_directory_identity ON c_versions(identity,node) WHERE until IS NULL AND identity IS NOT NULL AND dir=1 AND skipped=0;
            CREATE INDEX IF NOT EXISTS cv_review ON c_versions(mask,modified,size,node) WHERE until IS NULL AND mask<>0 AND skipped=0;
            CREATE INDEX IF NOT EXISTS cv_end ON c_versions(until,node) WHERE until IS NOT NULL;
            CREATE INDEX IF NOT EXISTS cv_first ON c_versions(first,node);
            """);
        using var root = InventoryStore.Command(connection, "SELECT node FROM c_directories WHERE path=$path", ("$path", _owner.RootPath));
        _root = root.ExecuteScalar() as long? ?? 0;
    }

    internal long Begin(SqliteConnection connection, long? basis)
    {
        using (var previous = InventoryStore.Command(connection, "SELECT max(id) FROM scans WHERE completed IS NOT NULL"))
            HasPrevious = previous.ExecuteScalar() is long;
        InventoryStore.Execute(connection, """
            CREATE TEMP TABLE IF NOT EXISTS seen_nodes(node INTEGER PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS scope_nodes(node INTEGER PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS changed_nodes(node INTEGER PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS affected_folders(node INTEGER PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS changed_identities(identity TEXT PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS prior_identities(identity TEXT PRIMARY KEY);
            DELETE FROM seen_nodes; DELETE FROM scope_nodes; DELETE FROM changed_nodes;
            DELETE FROM affected_folders; DELETE FROM changed_identities; DELETE FROM prior_identities;
            INSERT INTO scans(root) VALUES($root);
            """, ("$root", _owner.RootPath));
        using var id = InventoryStore.Command(connection, "SELECT last_insert_rowid()");
        var scan = (long)id.ExecuteScalar()!;
        using var catalog = new CompactCatalog(connection);
        _root = catalog.Directory(_owner.RootPath);
        _full = !basis.HasValue;
        _foldersVisited = 0;
        return scan;
    }

    internal IInventoryWriter CreateWriter(SqliteConnection connection, long scan) => new CompactWriter(this, connection, scan);

    internal void IncludeScope(SqliteConnection connection, string path)
    {
        var range = InventoryStore.SubtreeRange(path);
        // The old rows remain visible until enumeration ends. Unchanged metadata
        // can be reused; only unseen members of this scope become deletions.
        InventoryStore.Execute(connection, """
            INSERT OR IGNORE INTO scope_nodes
                SELECT n.id FROM (SELECT node FROM c_directories WHERE path=$path
                    UNION ALL SELECT node FROM c_directories WHERE path>$prefix AND path<$end) d
                CROSS JOIN c_nodes n ON n.parent=d.node CROSS JOIN c_versions v ON v.node=n.id AND v.until IS NULL;
            """, ("$path", range.Path), ("$prefix", range.Prefix), ("$end", range.End));
        var exact = Lookup(connection, range.Path);
        if (exact.HasValue) InventoryStore.Execute(connection, "INSERT OR IGNORE INTO scope_nodes VALUES($id)", ("$id", exact.Value));
    }

    internal void FinishEnumeration(SqliteConnection connection, long scan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HasPrevious) return;
        var scope = _full ? "" : " AND v.node IN (SELECT node FROM scope_nodes)";
        // Look up parents from the missing nodes. An ordinary join can scan
        // the full catalog in parent order to satisfy DISTINCT.
        InventoryStore.Execute(connection, $"""
            CREATE TEMP TABLE IF NOT EXISTS missing_nodes(node INTEGER PRIMARY KEY);
            DELETE FROM missing_nodes;
            INSERT INTO missing_nodes SELECT v.node FROM c_versions v INDEXED BY cv_current
                WHERE v.until IS NULL AND v.node NOT IN (SELECT node FROM seen_nodes){scope};
            INSERT OR IGNORE INTO changed_identities SELECT v.identity FROM missing_nodes m CROSS JOIN c_versions v INDEXED BY cv_current ON m.node=v.node
                WHERE v.until IS NULL AND v.dir=0 AND v.skipped=0 AND v.identity IS NOT NULL;
            INSERT OR IGNORE INTO prior_identities SELECT v.identity FROM missing_nodes m CROSS JOIN c_versions v INDEXED BY cv_current ON m.node=v.node
                WHERE v.until IS NULL AND v.dir=0 AND v.skipped=0 AND v.identity IS NOT NULL;
            INSERT OR IGNORE INTO affected_folders SELECT n.parent FROM missing_nodes m CROSS JOIN c_nodes n ON n.id=m.node WHERE n.id<>$root AND n.parent<>0;
            INSERT OR IGNORE INTO changed_nodes SELECT node FROM missing_nodes;
            UPDATE c_versions SET until=$scan WHERE until IS NULL AND node IN (SELECT node FROM missing_nodes);
            UPDATE c_spelling SET until=$scan WHERE until IS NULL AND node IN (SELECT node FROM missing_nodes);
            """, ("$root", _root), ("$scan", scan));
        token.ThrowIfCancellationRequested();
    }

    internal void MarkChanged(SqliteConnection connection, long node, long parent, bool directory)
    {
        if (HasPrevious) InventoryStore.Execute(connection, "INSERT OR IGNORE INTO changed_nodes VALUES($node)", ("$node", node));
        if (node != _root && parent != 0) InventoryStore.Execute(connection, "INSERT OR IGNORE INTO affected_folders VALUES($node)", ("$node", parent));
        if (directory) InventoryStore.Execute(connection, "INSERT OR IGNORE INTO affected_folders VALUES($node)", ("$node", node));
    }

    internal static void OwnVersion(SqliteConnection connection, long scan, long node)
    {
        // Copy at most this node, never the rest of the snapshot.
        InventoryStore.Execute(connection, """
            UPDATE c_versions SET until=$scan WHERE node=$node AND until IS NULL AND first<>$scan;
            INSERT OR IGNORE INTO c_versions
                SELECT node,$scan,NULL,parent,dir,depth,size,allocated,identity,modified,attributes,files,folders,unique_allocated,skipped,mask
                FROM c_versions WHERE node=$node AND until=$scan AND first<>$scan ORDER BY first DESC LIMIT 1;
            """, ("$scan", scan), ("$node", node));
    }

    internal void Complete(SqliteConnection connection, long scan, string method, JournalCursor? cursor,
        IReadOnlyList<ScanIssue> issues, CancellationToken token, IReadOnlyList<CleanupSuggestion> suggestions)
    {
        token.ThrowIfCancellationRequested();
        using var root = InventoryStore.Command(connection, "SELECT count(*) FROM c_versions WHERE node=$root AND until IS NULL AND dir=1 AND skipped=0", ("$root", _root));
        if ((long)root.ExecuteScalar()! != 1) throw new DirectoryNotFoundException("The inventory root could not be read. The previous snapshot was preserved.");
        using var prior = InventoryStore.Command(connection, "SELECT max(id) FROM scans WHERE id<$id AND completed IS NOT NULL", ("$id", scan));
        var previous = prior.ExecuteScalar() as long?;
        var changes = CompareChanged(connection, scan, previous, 5000, token);
        using var skipped = InventoryStore.Command(connection, "SELECT count(*) FROM c_versions INDEXED BY cv_skipped WHERE until IS NULL AND skipped=1");
        using var written = InventoryStore.Command(connection, "SELECT count(*) FROM c_versions INDEXED BY cv_first WHERE first=$id", ("$id", scan));
        var work = new ScanWork("Compact v3", (long)written.ExecuteScalar()!, _foldersVisited, _readers.Readers, _readers.Reused, _readers.ProbeSeconds);
        InventoryStore.Execute(connection, """
            UPDATE scans SET completed=$time,method=$method,journal=$journal,issues=$issues,skipped=$skipped,
                suggestions=$suggestions,changes=$changes,work=$work WHERE id=$id;
            """, ("$id", scan), ("$time", DateTime.UtcNow.ToString("O")), ("$method", method),
            ("$journal", cursor is null ? null : JsonSerializer.Serialize(cursor)), ("$issues", JsonSerializer.Serialize(issues)),
            ("$skipped", (long)skipped.ExecuteScalar()!), ("$suggestions", JsonSerializer.Serialize(suggestions)),
            ("$changes", JsonSerializer.Serialize(changes)), ("$work", JsonSerializer.Serialize(work)));
    }

    internal void Prune(SqliteConnection connection, HashSet<long> keep, List<long> remove)
    {
        using var schema = InventoryStore.Command(connection, "PRAGMA user_version");
        var cleanOldCatalog = (long)schema.ExecuteScalar()! < 4;
        if (remove.Count == 0 && !cleanOldCatalog) return;
        InventoryStore.Execute(connection, """
            BEGIN IMMEDIATE;
            CREATE TEMP TABLE IF NOT EXISTS kept_scans(id INTEGER PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS prune_nodes(node INTEGER PRIMARY KEY);
            DELETE FROM kept_scans; DELETE FROM prune_nodes;
            """);
        try
        {
            foreach (var id in keep) InventoryStore.Execute(connection, "INSERT INTO kept_scans VALUES($id)", ("$id", id));
            if (cleanOldCatalog)
                InventoryStore.Execute(connection, """
                    INSERT OR IGNORE INTO prune_nodes SELECT n.id FROM c_nodes n
                    WHERE NOT EXISTS(SELECT 1 FROM c_versions v WHERE v.node=n.id);
                    PRAGMA user_version=4;
                    """);
            var boundary = keep.Max();
            foreach (var id in remove)
            {
                // Usually this range contains just the preceding update's changed
                // rows. Releasing an old open view also releases its held versions.
                InventoryStore.Execute(connection, """
                    INSERT OR IGNORE INTO prune_nodes SELECT node FROM c_versions WHERE until>$old AND until<=$boundary AND first<=$old
                        AND NOT EXISTS(SELECT 1 FROM kept_scans k WHERE k.id>=first AND k.id<until);
                    DELETE FROM c_versions WHERE until>$old AND until<=$boundary AND first<=$old
                        AND NOT EXISTS(SELECT 1 FROM kept_scans k WHERE k.id>=first AND k.id<until);
                    DELETE FROM scans WHERE id=$old;
                    """, ("$old", id), ("$boundary", boundary));
            }
            // Drop unused leaves before their directory ancestors. A retained
            // version or a child still needs its catalog path.
            InventoryStore.Execute(connection, """
                WITH RECURSIVE ancestors(node) AS (
                    SELECT node FROM prune_nodes UNION
                    SELECT n.parent FROM c_nodes n JOIN ancestors a ON n.id=a.node WHERE n.parent<>0
                ) INSERT OR IGNORE INTO prune_nodes SELECT node FROM ancestors;
                CREATE TEMP TABLE IF NOT EXISTS pruned_leaves(node INTEGER PRIMARY KEY);
                DELETE FROM c_spelling WHERE until IS NOT NULL
                    AND NOT EXISTS(SELECT 1 FROM kept_scans k WHERE k.id>=first AND k.id<until);
                """);
            while (true)
            {
                InventoryStore.Execute(connection, """
                    DELETE FROM pruned_leaves;
                    INSERT INTO pruned_leaves SELECT p.node FROM prune_nodes p
                    WHERE NOT EXISTS(SELECT 1 FROM c_versions v WHERE v.node=p.node)
                      AND NOT EXISTS(SELECT 1 FROM c_nodes child WHERE child.parent=p.node);
                    """);
                using var count = InventoryStore.Command(connection, "SELECT EXISTS(SELECT 1 FROM pruned_leaves)");
                if ((long)count.ExecuteScalar()! == 0) break;
                InventoryStore.Execute(connection, """
                    DELETE FROM c_spelling WHERE node IN (SELECT node FROM pruned_leaves);
                    DELETE FROM c_directories WHERE node IN (SELECT node FROM pruned_leaves);
                    DELETE FROM c_nodes WHERE id IN (SELECT node FROM pruned_leaves);
                    DELETE FROM prune_nodes WHERE node IN (SELECT node FROM pruned_leaves);
                    """);
            }
            InventoryStore.Execute(connection, "COMMIT");
        }
        catch { InventoryStore.Execute(connection, "ROLLBACK"); throw; }
    }
}
