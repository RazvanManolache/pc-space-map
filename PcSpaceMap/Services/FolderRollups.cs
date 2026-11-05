using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

// Retain one compact record per directory, never one object per file.
internal static class FolderRollups
{
    internal struct Total
    {
        public string Path;
        public int Parent;
        public long Size, Allocated, Unique, Files, Folders;
        public bool UnknownAllocated, UnknownUnique;
        public readonly long? AllocatedBytes => UnknownAllocated ? null : Allocated;
        public readonly long? UniqueBytes => UnknownUnique ? null : Unique;
    }

    internal sealed record Result(List<Total> Folders, long OrphanFiles, long UniquePolicyMismatches, IReadOnlyList<CleanupSuggestion>? Suggestions);

    internal static Dictionary<string, string> DuplicateKeepers(SqliteConnection connection, long scan, CancellationToken token)
    {
        // entry_identity covers identity and the primary path. First identify duplicates without
        // probing the wide entries table for every unique file's dir flag.
        InventoryStore.Execute(connection, """
            CREATE TEMP TABLE IF NOT EXISTS duplicate_files(identity TEXT PRIMARY KEY,keeper TEXT);
            DELETE FROM duplicate_files;
            INSERT INTO duplicate_files(identity)
              SELECT identity FROM entries INDEXED BY entry_identity WHERE scan=$id AND identity IS NOT NULL
              GROUP BY identity HAVING count(*)>1;
            UPDATE duplicate_files SET keeper=(SELECT min(path) FROM entries INDEXED BY entry_identity
              WHERE scan=$id AND identity=duplicate_files.identity AND dir=0);
            DELETE FROM duplicate_files WHERE keeper IS NULL;
            """, ("$id", scan));
        token.ThrowIfCancellationRequested();
        using var command = InventoryStore.Command(connection, "SELECT identity,keeper FROM duplicate_files");
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    internal static Result? Calculate(SqliteConnection connection, long scan, CancellationToken token,
        IReadOnlyDictionary<string, string>? keepers = null, long memoryBudget = 768L * 1024 * 1024, bool collectCleanup = false)
    {
        var folders = new List<Total>();
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long estimatedMemory = 0;
        using (var directories = InventoryStore.Command(connection, "SELECT path,parent FROM entries WHERE scan=$id AND dir=1 ORDER BY path", ("$id", scan)))
        using (var reader = directories.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var path = reader.GetString(0);
                estimatedMemory += 2L * path.Length + 160;
                if (estimatedMemory > memoryBudget) return null;
                ids.Add(path, folders.Count);
                folders.Add(new Total { Path = path, Parent = ids.GetValueOrDefault(reader.GetString(1), -1) });
            }
        }
        var totals = CollectionsMarshal.AsSpan(folders);
        var cleanup = collectCleanup ? new CleanupAnalyzer() : null;
        long orphans = 0, mismatches = 0;
        using (var files = InventoryStore.Command(connection, """
            SELECT parent,size,allocated,unique_allocated,identity,path,modified FROM entries
            WHERE scan=$id AND dir=0 ORDER BY path
            """, ("$id", scan)))
        using (var reader = files.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                cleanup?.Consider(reader.GetString(5), reader.GetInt64(1), new DateTime(reader.GetInt64(6), DateTimeKind.Utc));
                var allocated = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                var unique = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
                if (keepers is not null)
                {
                    var expected = allocated;
                    if (!reader.IsDBNull(4) && keepers.TryGetValue(reader.GetString(4), out var keeper)
                        && !keeper.Equals(reader.GetString(5), StringComparison.OrdinalIgnoreCase)) expected = 0;
                    if (unique != expected) mismatches++;
                    unique = expected;
                }
                if (!ids.TryGetValue(reader.GetString(0), out var parent)) { orphans++; continue; }
                ref var total = ref totals[parent];
                checked
                {
                    total.Size += reader.GetInt64(1);
                    total.Files++;
                    if (allocated.HasValue) total.Allocated += allocated.Value; else total.UnknownAllocated = true;
                    if (unique.HasValue) total.Unique += unique.Value; else total.UnknownUnique = true;
                }
            }
        }
        // Paths are ordered, so each parent precedes its descendants. Reverse order is postorder.
        for (var i = totals.Length - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested();
            ref var child = ref totals[i];
            if (child.Parent < 0) continue;
            ref var parent = ref totals[child.Parent];
            checked
            {
                parent.Size += child.Size;
                parent.Files += child.Files;
                parent.Folders += child.Folders + 1;
                parent.Allocated += child.Allocated;
                parent.Unique += child.Unique;
            }
            parent.UnknownAllocated |= child.UnknownAllocated;
            parent.UnknownUnique |= child.UnknownUnique;
        }
        return new(folders, orphans, mismatches, cleanup?.Build());
    }

    internal static void Save(SqliteConnection connection, long scan, Result result, CancellationToken token)
    {
        using var command = InventoryStore.Command(connection, """
            UPDATE entries SET size=$size,allocated=$allocated,unique_allocated=$unique,files=$files,folders=$folders
            WHERE scan=$id AND path=$path AND
              (size<>$size OR allocated IS NOT $allocated OR unique_allocated IS NOT $unique OR files<>$files OR folders<>$folders)
            """, ("$size", 0L), ("$allocated", null), ("$unique", null), ("$files", 0L), ("$folders", 0L), ("$id", scan), ("$path", ""));
        foreach (var folder in result.Folders)
        {
            token.ThrowIfCancellationRequested();
            command.Parameters[0].Value = folder.Size;
            command.Parameters[1].Value = folder.AllocatedBytes is { } allocated ? allocated : DBNull.Value;
            command.Parameters[2].Value = folder.UniqueBytes is { } unique ? unique : DBNull.Value;
            command.Parameters[3].Value = folder.Files;
            command.Parameters[4].Value = folder.Folders;
            command.Parameters[6].Value = folder.Path;
            command.ExecuteNonQuery();
        }
    }
}
