using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

internal static class CompactImport
{
    internal static bool Import(InventoryStore destination, string legacyPath, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        source.Open();
        InventoryStore.Execute(source, "PRAGMA cache_size=-65536; BEGIN");
        using var latest = InventoryStore.Command(source, "SELECT max(id) FROM scans WHERE completed IS NOT NULL");
        if (latest.ExecuteScalar() is not long previous) return false;
        using var details = InventoryStore.Command(source, "SELECT issues,journal,root FROM scans WHERE id=$id", ("$id", previous));
        List<ScanIssue> issues;
        JournalCursor? cursor;
        using (var reader = details.ExecuteReader())
        {
            if (!reader.Read()) return false;
            if (!InventoryStore.Normalize(reader.GetString(2)).Equals(destination.RootPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The saved inventory belongs to a different root.");
            issues = JsonSerializer.Deserialize<List<ScanIssue>>(reader.GetString(0)) ?? [];
            cursor = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<JournalCursor>(reader.GetString(1));
        }
        using var connection = destination.Open();
        InventoryStore.Execute(connection, "BEGIN IMMEDIATE");
        var committed = false;
        try
        {
            using var exists = InventoryStore.Command(connection, "SELECT max(id) FROM scans WHERE completed IS NOT NULL");
            if (exists.ExecuteScalar() is long) { InventoryStore.Execute(connection, "ROLLBACK"); return false; }
            var clock = Stopwatch.StartNew();
            destination.SetReaderSelection(new(0, false, false));
            var scan = destination.Begin(connection, null);
            long files = 0, directories = 0, bytes = 0;
            using (var writer = destination.CreateWriter(connection, scan, true))
            {
                using var entries = InventoryStore.Command(source, """
                    SELECT path,parent,name,dir,depth,size,allocated,identity,modified,attributes
                    FROM entries WHERE scan=$id ORDER BY path
                    """, ("$id", previous));
                using (var reader = entries.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        var directory = reader.GetInt64(3) != 0;
                        var entry = new InventoryEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), directory,
                            directory ? 0 : reader.GetInt64(5), directory ? 0 : reader.IsDBNull(6) ? null : reader.GetInt64(6),
                            reader.IsDBNull(7) ? null : reader.GetString(7), new DateTime(reader.GetInt64(8), DateTimeKind.Utc), (FileAttributes)reader.GetInt64(9));
                        writer.Write(entry, reader.GetInt32(4));
                        if (directory) directories++; else { files++; bytes += entry.Size; }
                        if ((files + directories) % 1000 == 0) progress?.Report(new() { Phase = "Importing saved metadata", CurrentPath = entry.Path,
                            FileCount = files, DirectoryCount = directories, BytesFound = bytes });
                    }
                }
                using var skipped = InventoryStore.Command(source, "SELECT path FROM skipped_entries WHERE scan=$id", ("$id", previous));
                using var links = skipped.ExecuteReader();
                while (links.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var path = links.GetString(0);
                    var depth = Path.GetRelativePath(destination.RootPath, path).Count(c => c == '\\') + 1;
                    writer.Write(new(path, Path.GetDirectoryName(path) ?? "", Path.GetFileName(path), false, 0, null, null,
                        DateTime.MinValue, FileAttributes.ReparsePoint), depth, true);
                }
            }
            destination.FinishEnumeration(connection, scan, token);
            var suggestions = destination.AggregateSnapshot(connection, scan, token, 0);
            destination.CompleteSnapshot(connection, scan, "Imported saved metadata", cursor, issues, token, suggestions);
            token.ThrowIfCancellationRequested();
            InventoryStore.Execute(connection, "COMMIT");
            committed = true;
            destination.SaveTimings(scan, [new("Import saved metadata", clock.Elapsed.TotalSeconds)]);
            return true;
        }
        catch { if (!committed) InventoryStore.Execute(connection, "ROLLBACK"); throw; }
    }
}
