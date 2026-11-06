using Microsoft.Data.Sqlite;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

internal sealed partial class CompactInventory
{
    private const string Name = "coalesce(sp.name,n.name)";
    private const string ParentPath = "coalesce(ps.path,d.path)";
    private const string DisplayPath = "coalesce(sp.path,CASE WHEN n.parent=0 THEN " + Name
        + " WHEN substr(" + ParentPath + ",-1)='\\' THEN " + ParentPath + "||" + Name + " ELSE " + ParentPath + "||'\\'||" + Name + " END)";
    private const string Projection = DisplayPath + " AS path," + Name + ",v.dir,v.size,v.modified,v.attributes,v.files,v.folders,v.allocated,v.unique_allocated";
    private static string SpellingJoins(long scan, bool current) =>
        $" LEFT JOIN c_spelling sp ON sp.node=n.id AND {At(scan, current, "sp")} LEFT JOIN c_spelling ps ON ps.node=n.parent AND {At(scan, current, "ps")} ";
    private static string JoinsAt(long scan, bool current) =>
        " JOIN c_nodes n ON n.id=v.node LEFT JOIN c_directories d ON d.node=n.parent " + SpellingJoins(scan, current);
    private SqliteConnection OpenRead()
    {
        var connection = _owner.Open();
        try { InventoryStore.Execute(connection, "BEGIN"); return connection; }
        catch { connection.Dispose(); throw; }
    }
    internal (ScanNode Root, IReadOnlyList<ScanNode> Largest) LoadNodes(SqliteConnection connection, long scan) =>
        (FindOn(connection, scan, _owner.RootPath) ?? throw new InvalidOperationException("Inventory root missing."), LargestOn(connection, scan, null, 5000));
    internal (IReadOnlyList<CleanupSuggestion> Suggestions, long Skipped) LoadFolderReview(
        SqliteConnection connection, long scan, string folder, DateTime completed)
    {
        var current = Current(connection, scan);
        var range = InventoryStore.SubtreeRange(folder);
        using var command = InventoryStore.Command(connection, $"""
            SELECT count(*) FROM c_versions v WHERE {At(scan, current)} AND v.skipped=1 AND v.parent IN
                (SELECT node FROM c_directories WHERE path=$path OR (path>$prefix AND path<$end))
            """, ("$path", range.Path), ("$prefix", range.Prefix), ("$end", range.End));
        return (ReviewSuggestions(connection, scan, current, folder, completed, default), (long)command.ExecuteScalar()!);
    }
    private static bool Current(SqliteConnection connection, long scan)
    {
        using var command = InventoryStore.Command(connection, "SELECT max(id) FROM scans");
        return command.ExecuteScalar() is long latest && latest == scan;
    }
    private static string At(long scan, bool current, string alias = "v") => current
        ? alias + ".until IS NULL" : $"{alias}.first<={scan} AND ({alias}.until IS NULL OR {alias}.until>{scan})";
    private static long? Lookup(SqliteConnection connection, string path)
    {
        path = InventoryStore.Normalize(path);
        var parent = Path.GetDirectoryName(path) ?? "";
        using var command = InventoryStore.Command(connection, parent.Length == 0
            ? "SELECT id FROM c_nodes WHERE parent=0 AND name=$name"
            : "SELECT n.id FROM c_nodes n JOIN c_directories d ON d.node=n.parent WHERE d.path=$parent AND n.name=$name",
            ("$parent", parent), ("$name", parent.Length == 0 ? path : Path.GetFileName(path)));
        return command.ExecuteScalar() as long?;
    }
    internal static bool HasDirectory(SqliteConnection connection, long scan, string path)
    {
        using var command = InventoryStore.Command(connection, """
            SELECT 1 FROM c_directories d JOIN c_versions v ON v.node=d.node
            WHERE d.path=$path AND v.first<=$scan AND (v.until IS NULL OR v.until>$scan) AND v.dir=1 AND v.skipped=0 LIMIT 1
            """, ("$path", path), ("$scan", scan));
        return command.ExecuteScalar() is not null;
    }
    private static string PathFor(SqliteConnection connection, long node, long scan)
    {
        using var command = InventoryStore.Command(connection, $"SELECT {DisplayPath} FROM c_nodes n LEFT JOIN c_directories d ON d.node=n.parent {SpellingJoins(scan, true)} WHERE n.id=$node", ("$node", node));
        return command.ExecuteScalar() as string ?? throw new InvalidOperationException("Inventory path is missing.");
    }
    internal ScanNode? Find(long scan, string path)
    {
        using var connection = OpenRead();
        return FindOn(connection, scan, path);
    }
    private ScanNode? FindOn(SqliteConnection connection, long scan, string path)
    {
        var id = Lookup(connection, path);
        if (id is null) return null;
        var current = Current(connection, scan);
        using var command = InventoryStore.Command(connection, $"SELECT {Projection} FROM c_versions v {JoinsAt(scan, current)} WHERE v.node=$node AND {At(scan, current)} AND v.skipped=0",
            ("$node", id.Value));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var result = _owner.Node(reader, scan);
        reader.Close();
        if (!result.Path.Equals(_owner.RootPath, StringComparison.OrdinalIgnoreCase))
            result.Parent = FindOn(connection, scan, Path.GetDirectoryName(result.Path)!);
        return result;
    }
    internal long ChildCount(long scan, string path)
    {
        using var connection = OpenRead();
        var id = Lookup(connection, path);
        if (id is null) return 0;
        var current = Current(connection, scan);
        var from = current ? "c_versions v INDEXED BY cv_parent" : "c_nodes n CROSS JOIN c_versions v ON v.node=n.id";
        var parent = current ? "v.parent" : "n.parent";
        using var command = InventoryStore.Command(connection, $"SELECT count(*) FROM {from} WHERE {parent}=$parent AND {At(scan, current)} AND v.skipped=0", ("$parent", id.Value));
        return (long)command.ExecuteScalar()!;
    }
    internal List<ScanNode> Children(long scan, ScanNode parent, int offset, int limit, FileFilter? nameFilter, CancellationToken token)
    {
        using var connection = OpenRead();
        var id = Lookup(connection, parent.Path);
        if (id is null) return [];
        var current = Current(connection, scan);
        var condition = InventoryStore.NameFilterCondition(connection, nameFilter, Name);
        using var command = InventoryStore.Command(connection, $"""
            SELECT {Projection} FROM c_versions v {(current ? "INDEXED BY cv_parent" : "")}{JoinsAt(scan, current)}
            WHERE v.parent=$parent AND {At(scan, current)} AND v.skipped=0{condition} ORDER BY v.size DESC,n.name,v.node LIMIT $limit OFFSET $offset
            """, ("$parent", id.Value), ("$limit", limit), ("$offset", offset));
        InventoryStore.ObserveQueryCancellation(connection, token);
        return InventoryStore.ReadNodes(command, reader => _owner.Node(reader, scan, parent), token);
    }

    internal IReadOnlyList<ScanNode> SearchNames(long scan, string under, FileFilter nameFilter, int offset, int limit, CancellationToken token)
    {
        using var connection = OpenRead();
        var current = Current(connection, scan);
        var range = InventoryStore.SubtreeRange(under);
        var global = range.Path.Equals(_owner.RootPath, StringComparison.OrdinalIgnoreCase);
        var scope = global ? " AND v.node<>$root" : " AND v.parent IN (SELECT node FROM c_directories WHERE path=$path OR (path>$prefix AND path<$end))";
        var condition = InventoryStore.NameFilterCondition(connection, nameFilter, Name)
            + InventoryStore.ModificationFilterCondition(nameFilter, "v.modified");
        using var command = InventoryStore.Command(connection, $"""
            SELECT {Projection} FROM c_versions v {(current ? "INDEXED BY " + (global ? "cv_current" : "cv_parent") : "")}{JoinsAt(scan, current)}
            WHERE {At(scan, current)} AND v.skipped=0{scope}{condition}
            ORDER BY v.node LIMIT $limit OFFSET $offset
            """, ("$root", global ? Lookup(connection, _owner.RootPath) : null), ("$path", range.Path), ("$prefix", range.Prefix),
            ("$end", range.End), ("$limit", limit), ("$offset", offset),
            ("$before", nameFilter.ModifiedBefore?.Ticks ?? 0), ("$since", nameFilter.ModifiedSince?.Ticks ?? 0));
        InventoryStore.ObserveQueryCancellation(connection, token);
        return InventoryStore.ReadNodes(command, reader => _owner.Node(reader, scan), token);
    }
    internal IReadOnlyList<ScanNode> Largest(long scan, string? under, int limit, FileFilter? filter, CancellationToken token)
    {
        using var connection = OpenRead();
        return LargestOn(connection, scan, under, limit, filter, token);
    }
    private IReadOnlyList<ScanNode> LargestOn(SqliteConnection connection, long scan, string? under, int limit, FileFilter? filter = null,
        CancellationToken token = default)
    {
        var current = Current(connection, scan);
        var range = InventoryStore.SubtreeRange(under ?? _owner.RootPath);
        var global = range.Path.Equals(_owner.RootPath, StringComparison.OrdinalIgnoreCase);
        var condition = global ? "" : " AND v.parent IN (SELECT node FROM c_directories WHERE path=$path OR (path>$prefix AND path<$end))";
        // MATERIALIZED prevents the size index from searching the whole drive for
        // a tiny subtree. Global requests retain their bounded index walk.
        var fileFilter = InventoryStore.FileFilterCondition(connection, filter, "v.size", "v.modified", Name);
        var rows = $"SELECT {Projection} FROM c_versions v {(current ? "INDEXED BY " + (global ? "cv_size" : "cv_parent") : "")}{JoinsAt(scan, current)} WHERE {At(scan, current)} AND v.skipped=0 AND v.dir=0{condition}{fileFilter}";
        var sql = global ? rows + " ORDER BY v.size DESC LIMIT $limit" : $"WITH selected AS MATERIALIZED ({rows}) SELECT * FROM selected ORDER BY size DESC,path LIMIT $limit";
        using var command = InventoryStore.Command(connection, sql, ("$path", range.Path), ("$prefix", range.Prefix), ("$end", range.End), ("$limit", limit),
            ("$minBytes", filter?.MinimumBytes ?? 0), ("$before", filter?.ModifiedBefore?.Ticks ?? 0),
            ("$since", filter?.ModifiedSince?.Ticks ?? 0));
        InventoryStore.ObserveQueryCancellation(connection, token);
        return InventoryStore.ReadNodes(command, reader => _owner.Node(reader, scan), token);
    }
    internal IEnumerable<ScanNode> Enumerate(long scan, string? under)
    {
        using var connection = OpenRead();
        var current = Current(connection, scan);
        var range = InventoryStore.SubtreeRange(under ?? _owner.RootPath);
        var exact = Lookup(connection, range.Path) ?? -1;
        using var command = InventoryStore.Command(connection, $"""
            SELECT {Projection} FROM c_versions v {JoinsAt(scan, current)}
            WHERE {At(scan, current)} AND v.skipped=0 AND v.node=$exact
            UNION ALL SELECT {Projection} FROM c_versions v {(current ? "INDEXED BY cv_parent" : "")}{JoinsAt(scan, current)}
            WHERE {At(scan, current)} AND v.skipped=0 AND v.node<>$exact AND v.parent IN
                (SELECT node FROM c_directories WHERE path=$path OR (path>$prefix AND path<$end))
            """, ("$path", range.Path), ("$prefix", range.Prefix), ("$end", range.End), ("$exact", exact));
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return _owner.Node(reader, scan);
    }
    internal IReadOnlyList<string> IdentityPaths(SqliteConnection connection, long scan, string identity, bool directory)
    {
        var current = Current(connection, scan);
        var index = current ? "INDEXED BY " + (directory ? "cv_directory_identity" : "cv_identity") : "";
        using var command = InventoryStore.Command(connection, $"SELECT {DisplayPath} FROM c_versions v {index}{JoinsAt(scan, current)} WHERE {At(scan, current)} AND v.identity=$identity AND v.dir={(directory ? 1 : 0)} AND v.skipped=0",
            ("$identity", identity));
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    internal bool FolderUnchanged(string folder, long before, long after)
    {
        using var connection = OpenRead();
        var range = InventoryStore.SubtreeRange(folder);
        // Snapshot intervals are index-maintained revisions. Check both ends so
        // deletions and edits with unchanged folder size still invalidate the view.
        using var query = InventoryStore.Command(connection, """
            SELECT CASE WHEN EXISTS(
                SELECT 1 FROM scans old JOIN scans new ON new.id=$after
                WHERE old.id=$before AND old.completed IS NOT NULL AND new.completed IS NOT NULL AND old.issues=new.issues
            ) AND NOT EXISTS(
                SELECT 1 FROM scans WHERE id>$before AND id<=$after AND method NOT IN ('Folder update','NTFS journal update')
            ) AND NOT EXISTS(
                SELECT 1 FROM c_spelling WHERE (first>$before AND first<=$after) OR (until>$before AND until<=$after)
            ) THEN NOT EXISTS(
                SELECT 1 FROM c_versions v INDEXED BY cv_first LEFT JOIN c_directories d ON d.node=v.parent
                WHERE v.first>$before AND v.first<=$after
                    AND (v.node=$folder OR d.path=$path OR (d.path>$prefix AND d.path<$end))
                UNION ALL
                SELECT 1 FROM c_versions v INDEXED BY cv_end LEFT JOIN c_directories d ON d.node=v.parent
                WHERE v.until>$before AND v.until<=$after
                    AND (v.node=$folder OR d.path=$path OR (d.path>$prefix AND d.path<$end))
            ) ELSE 0 END
            """, ("$before", before), ("$after", after), ("$folder", Lookup(connection, range.Path)),
            ("$path", range.Path), ("$prefix", range.Prefix), ("$end", range.End));
        return (long)query.ExecuteScalar()! == 1;
    }

    private static IReadOnlyList<InventoryChange> CompareChanged(SqliteConnection connection, long scan, long? previous, int limit, CancellationToken token)
    {
        if (!previous.HasValue) return [];
        return CompareRows(connection, scan, previous.Value, limit, true, token);
    }
    internal IReadOnlyList<InventoryChange> Compare(long scan, long? previous, int limit)
    {
        if (!previous.HasValue) return [];
        using var connection = _owner.Open();
        return CompareRows(connection, scan, previous.Value, limit, false, default);
    }
    private static IReadOnlyList<InventoryChange> CompareRows(SqliteConnection connection, long scan, long previous, int limit, bool changedOnly, CancellationToken token)
    {
        var nodes = changedOnly ? "SELECT node FROM changed_nodes" : "SELECT node FROM c_versions WHERE first<=$a UNION SELECT node FROM c_versions WHERE first<=$b";
        var afterPath = DisplayPath.Replace("sp.", "sa.").Replace("ps.", "pa.");
        var beforePath = DisplayPath.Replace("sp.", "sb.").Replace("ps.", "pb.");
        using var command = InventoryStore.Command(connection, $"""
            SELECT n.id,CASE WHEN a.node IS NULL THEN {beforePath} ELSE {afterPath} END,a.size,a.modified,a.dir,b.size,b.modified,b.dir
            FROM ({nodes}) selected JOIN c_nodes n ON n.id=selected.node LEFT JOIN c_directories d ON d.node=n.parent
            LEFT JOIN c_versions a ON a.node=n.id AND a.first<=$a AND (a.until IS NULL OR a.until>$a) AND a.skipped=0
            LEFT JOIN c_versions b ON b.node=n.id AND b.first<=$b AND (b.until IS NULL OR b.until>$b) AND b.skipped=0
            LEFT JOIN c_spelling sa ON sa.node=n.id AND sa.first<=$a AND (sa.until IS NULL OR sa.until>$a)
            LEFT JOIN c_spelling sb ON sb.node=n.id AND sb.first<=$b AND (sb.until IS NULL OR sb.until>$b)
            LEFT JOIN c_spelling pa ON pa.node=n.parent AND pa.first<=$a AND (pa.until IS NULL OR pa.until>$a)
            LEFT JOIN c_spelling pb ON pb.node=n.parent AND pb.first<=$b AND (pb.until IS NULL OR pb.until>$b)
            WHERE (a.node IS NOT NULL OR b.node IS NOT NULL)
                AND (a.node IS NULL OR b.node IS NULL OR a.size<>b.size OR a.modified<>b.modified OR a.dir<>b.dir
                    OR coalesce(sa.name,n.name) COLLATE BINARY <> coalesce(sb.name,n.name) OR sa.path IS NOT sb.path)
            """, ("$a", scan), ("$b", previous));
        using var reader = command.ExecuteReader();
        IEnumerable<InventoryChange> Read()
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var added = reader.IsDBNull(5);
                var deleted = reader.IsDBNull(2);
                yield return new(reader.GetString(1), added ? "Added" : deleted ? "Deleted" : "Changed",
                    added ? 0 : reader.GetInt64(5), deleted ? 0 : reader.GetInt64(2));
            }
        }
        return SnapshotComparison.Best(Read(), limit);
    }
}
