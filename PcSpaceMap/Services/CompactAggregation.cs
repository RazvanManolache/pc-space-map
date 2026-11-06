using Microsoft.Data.Sqlite;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

internal sealed partial class CompactInventory
{
    private sealed record Alias(long Node, long Parent, long? Allocated, long? Unique, long First, string Path, long Size, long Modified, long Attributes);
    internal IReadOnlyList<CleanupSuggestion> Aggregate(SqliteConnection connection, long scan, CancellationToken token)
    {
        // Only identities touched by this scan can change hard-link ownership.
        var duplicateGroups = _full
            ? "SELECT identity FROM c_versions INDEXED BY cv_identity WHERE until IS NULL AND identity IS NOT NULL AND dir=0 AND skipped=0 GROUP BY identity HAVING count(*)>1"
            : "SELECT v.identity FROM changed_identities t CROSS JOIN c_versions v INDEXED BY cv_identity ON v.identity=t.identity WHERE v.until IS NULL AND v.identity IS NOT NULL AND v.dir=0 AND v.skipped=0 GROUP BY v.identity HAVING count(*)>1";
        using (var identities = InventoryStore.Command(connection, $"""
            {duplicateGroups}
            UNION SELECT p.identity FROM prior_identities p WHERE EXISTS(SELECT 1 FROM c_versions v INDEXED BY cv_identity
                WHERE v.identity=p.identity AND v.until IS NULL AND v.dir=0 AND v.skipped=0 AND v.identity IS NOT NULL AND v.unique_allocated IS NOT v.allocated)
            """))
        using (var reader = identities.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                using var aliases = InventoryStore.Command(connection, $"""
                    SELECT v.node,v.parent,v.allocated,v.unique_allocated,v.first,{DisplayPath} AS path,v.size,v.modified,v.attributes
                    FROM c_versions v INDEXED BY cv_identity {JoinsAt(scan, true)}
                    WHERE v.until IS NULL AND v.dir=0 AND v.skipped=0 AND v.identity=$identity
                    ORDER BY path COLLATE NOCASE
                    """, ("$identity", reader.GetString(0)));
                var rows = new List<Alias>();
                using (var group = aliases.ExecuteReader())
                    while (group.Read()) rows.Add(new(group.GetInt64(0), group.GetInt64(1), group.IsDBNull(2) ? null : group.GetInt64(2), group.IsDBNull(3) ? null : group.GetInt64(3), group.GetInt64(4), group.GetString(5), group.GetInt64(6), group.GetInt64(7), group.GetInt64(8)));
                var observed = rows.Where(row => row.First == scan).OrderByDescending(row => row.Modified).FirstOrDefault();
                var classifier = new CleanupAnalyzer();
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var allocated = observed is null ? row.Allocated : observed.Allocated;
                    var desired = i == 0 ? allocated : 0;
                    // Other names of this same physical file can be outside the
                    // requested folder. Keep their observed file metadata in sync.
                    var metadataChanged = observed is not null && (observed.Size != row.Size || observed.Modified != row.Modified
                        || observed.Allocated != row.Allocated || observed.Attributes != row.Attributes);
                    if (desired == row.Unique && !metadataChanged) continue;
                    if (row.First != scan) OwnVersion(connection, scan, row.Node);
                    if (metadataChanged)
                        InventoryStore.Execute(connection, "UPDATE c_versions SET size=$size,allocated=$allocated,modified=$modified,attributes=$attributes,mask=$mask,unique_allocated=$unique WHERE node=$node AND first=$scan",
                            ("$size", observed!.Size), ("$allocated", allocated), ("$modified", observed.Modified), ("$attributes", observed.Attributes),
                            ("$mask", classifier.MatchMask(row.Path, observed.Size)), ("$unique", desired), ("$node", row.Node), ("$scan", scan));
                    else InventoryStore.Execute(connection, "UPDATE c_versions SET unique_allocated=$value WHERE node=$node AND first=$scan",
                            ("$value", desired), ("$node", row.Node), ("$scan", scan));
                    MarkChanged(connection, row.Node, row.Parent, false);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        InventoryStore.Execute(connection, """
            WITH RECURSIVE ancestors(node) AS (
                SELECT node FROM affected_folders
                UNION SELECT n.parent FROM c_nodes n JOIN ancestors a ON n.id=a.node WHERE a.node<>$root AND n.parent<>0
            ) INSERT OR IGNORE INTO affected_folders SELECT node FROM ancestors;
            """, ("$root", _root));
        // Drive the join from the affected nodes. Starting from cv_size would
        // read every active directory even for a single-file journal update.
        using var folders = InventoryStore.Command(connection, """
            SELECT v.node,v.parent,v.size,v.allocated,v.unique_allocated,v.files,v.folders,v.first
            FROM affected_folders a CROSS JOIN c_versions v INDEXED BY cv_current ON v.node=a.node
            WHERE v.until IS NULL AND v.dir=1 AND v.skipped=0 ORDER BY v.depth DESC,v.node DESC
            """);
        using var totals = new NativeStatement(connection, """
            SELECT coalesce(sum(size),0),CASE WHEN count(allocated)=count(*) THEN coalesce(sum(allocated),0) END,
                CASE WHEN count(unique_allocated)=count(*) THEN coalesce(sum(unique_allocated),0) END,
                coalesce(sum(files),0),coalesce(sum(folders+dir),0)
            FROM c_versions INDEXED BY cv_parent WHERE parent=?1 AND until IS NULL AND skipped=0
            """);
        using var save = new NativeStatement(connection, """
            UPDATE c_versions SET size=?1,allocated=?2,unique_allocated=?3,files=?4,folders=?5 WHERE node=?6 AND first=?7
            """);
        using (var reader = folders.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                _foldersVisited++;
                var node = reader.GetInt64(0);
                totals.Reset(); totals.Number(1, node);
                if (!totals.Step()) throw new InvalidOperationException("Folder total missing.");
                var size = totals.Number(0); var allocated = totals.Optional(1); var unique = totals.Optional(2);
                var files = totals.Number(3); var count = totals.Number(4);
                totals.Reset();
                if (size == reader.GetInt64(2) && allocated == (reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3))
                    && unique == (reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4)) && files == reader.GetInt64(5) && count == reader.GetInt64(6)) continue;
                if (reader.GetInt64(7) != scan) OwnVersion(connection, scan, node);
                save.Reset(); save.Number(1, size); save.Optional(2, allocated); save.Optional(3, unique);
                save.Number(4, files); save.Number(5, count); save.Number(6, node); save.Number(7, scan); save.Step();
                MarkChanged(connection, node, reader.GetInt64(1), true);
            }
        }
        return ReviewSuggestions(connection, scan, true, null, DateTime.UtcNow, token);
    }

    internal IReadOnlyList<CleanupSuggestion> FilterCleanup(long scan, string under, DateTime completed, FileFilter filter, CancellationToken token)
    {
        using var connection = OpenRead();
        var current = Current(connection, scan);
        InventoryStore.ObserveQueryCancellation(connection, token);
        try { return ReviewSuggestions(connection, scan, current, under, completed, token, filter); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9 && token.IsCancellationRequested)
        { throw new OperationCanceledException(token); }
    }

    private IReadOnlyList<CleanupSuggestion> ReviewSuggestions(SqliteConnection connection, long scan, bool current,
        string? folder, DateTime now, CancellationToken token, FileFilter? nameFilter = null)
    {
        // Only paths that can match a cleanup rule have a review-index entry.
        // Thresholds are evaluated now, so unchanged files still age correctly.
        var thresholds = CleanupAnalyzer.RuleDays.Select(days => now.AddDays(-days).Ticks).ToArray();
        var buckets = new Dictionary<int, (long Size, long Count, long LargestSize, long Node)>();
        var index = current ? "INDEXED BY cv_review" : "";
        var range = InventoryStore.SubtreeRange(folder ?? _owner.RootPath);
        var scope = folder is null || range.Path.Equals(_owner.RootPath, StringComparison.OrdinalIgnoreCase)
            ? "" : " AND v.parent IN (SELECT node FROM c_directories WHERE path=$path OR (path>$prefix AND path<$end))";
        var filter = InventoryStore.NameFilterCondition(connection, nameFilter, Name);
        var joins = nameFilter is null ? "" : JoinsAt(scan, current);
        using (var candidates = InventoryStore.Command(connection,
            $"SELECT v.mask,v.modified,v.size,v.node FROM c_versions v {index}{joins} WHERE {At(scan, current)} AND v.skipped=0 AND v.mask<>0{scope}{filter}",
            ("$path", range.Path), ("$prefix", range.Prefix), ("$end", range.End)))
        using (var reader = candidates.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var mask = reader.GetInt32(0); var modified = reader.GetInt64(1);
                var code = 0;
                for (var i = 0; i < thresholds.Length; i++)
                    if ((mask & (1 << i)) != 0 && (CleanupAnalyzer.RuleDays[i] == 0 || modified <= thresholds[i])) { code = i + 1; break; }
                if (code == 0) continue;
                var size = reader.GetInt64(2); var node = reader.GetInt64(3);
                var bucket = buckets.GetValueOrDefault(code);
                buckets[code] = (checked(bucket.Size + size), bucket.Count + 1, Math.Max(bucket.LargestSize, size), size > bucket.LargestSize ? node : bucket.Node);
            }
        }
        return buckets.Select(pair => CleanupAnalyzer.FromCode(pair.Key, pair.Value.Size, pair.Value.Count, PathFor(connection, pair.Value.Node, scan)))
            .OrderByDescending(x => x.Size).ToList();
    }
}
