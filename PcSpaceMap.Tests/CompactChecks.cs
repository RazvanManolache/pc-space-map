using System.Security.Cryptography;
using PcSpaceMap.Services;
using PcSpaceMap.Models;

internal static class CompactChecks
{
    internal static async Task RunAsync(string suite, Action<bool, string> check)
    {
        var root = Path.Combine(suite, "compact-records");
        var changed = Path.Combine(root, "changed");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(changed); Directory.CreateDirectory(other);
        var file = Path.Combine(changed, "file.dat");
        File.WriteAllBytes(file, new byte[10]);
        for (var i = 0; i < 100; i++) File.WriteAllBytes(Path.Combine(other, i + ".dat"), new byte[1]);
        var scanner = new DiskScanner { DatabasePath = Path.Combine(suite, "compact-records.sqlite"), PreferNtfs = false };
        var first = await scanner.ScanAsync(root, null, default);
        File.WriteAllBytes(file, new byte[2000]);
        var updated = await scanner.RefreshAsync(first, changed, null, default);
        check(updated.Root.Size == 2100 && updated.Work is { VersionsWritten: <= 4, FoldersRecalculated: 2 },
            "one-file folder refresh writes only changed records and its two ancestors");
        check(updated.Inventory!.FolderUnchanged(other, first.SnapshotId, updated.SnapshotId)
            && !updated.Inventory.FolderUnchanged(changed, first.SnapshotId, updated.SnapshotId),
            "folder revision ignores unrelated changes and detects changes inside the view");
        var same = await scanner.RefreshAsync(updated, changed, null, default);
        check(same.Work is { VersionsWritten: 0, FoldersRecalculated: 0 } && same.Changes.Count == 0,
            "unchanged folder refresh reuses all existing versions without recalculating folders");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        var touched = await scanner.RefreshAsync(same, file, null, default);
        check(touched.Root.Size == same.Root.Size && !touched.Inventory!.FolderUnchanged(changed, same.SnapshotId, touched.SnapshotId),
            "folder revision detects a metadata edit with unchanged folder totals");
        var full = await scanner.ScanAsync(root, null, default);
        if (full.Work is not { VersionsWritten: 0, FoldersRecalculated: 0 })
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(full.Work));
            foreach (var difference in full.Changes)
            {
                var oldNode = same.Inventory!.Find(same.SnapshotId, difference.Path);
                var newNode = full.Inventory!.Find(full.SnapshotId, difference.Path);
                Console.WriteLine($"FULL CHANGE {difference.Path}: {difference.BeforeBytes}->{difference.AfterBytes}; {oldNode?.LastWriteUtc:O}->{newNode?.LastWriteUtc:O}; {oldNode?.Attributes}->{newNode?.Attributes}");
            }
        }
        check(full.Work is { VersionsWritten: 0, FoldersRecalculated: 0 } && full.Root.Size == 2100,
            "unchanged full scan shares its previous metadata versions");
        check(first.Inventory!.Find(first.SnapshotId, file)!.Size == 10 && first.Inventory.Find(first.SnapshotId, root)!.Size == 110,
            "shared storage preserves old file and folder values after later updates");
        var rebased = await scanner.RefreshAsync(first, changed, null, default);
        check(rebased.Root.Size == full.Root.Size && rebased.ScanMethod == "Folder update" && rebased.Work!.VersionsWritten == 0,
            "refreshing an old view rebases onto the latest inventory without a whole-drive rescan");
        File.WriteAllBytes(file, new byte[3000]);
        using (var cancelled = new CancellationTokenSource())
        {
            try
            {
                await scanner.RefreshAsync(rebased, changed, new InlineProgress(p =>
                { if (p.Phase == "Cleanup review and comparison") cancelled.Cancel(); }), cancelled.Token);
                check(false, "compact late cancellation");
            }
            catch (OperationCanceledException)
            {
                var loaded = await scanner.LoadSavedAsync(root);
                check(loaded!.SnapshotId == rebased.SnapshotId && loaded.Root.Size == 2100,
                    "cancellation after summary updates rolls back all new version intervals");
            }
        }
        var final = await scanner.RefreshAsync(rebased, changed, null, default);
        using (var connection = final.Inventory!.Open())
        {
            using var paths = InventoryStore.Command(connection, "SELECT count(*) FROM c_directories");
            using var metadata = InventoryStore.Command(connection, "SELECT count(*) FROM pragma_table_info('c_versions') WHERE name='path'");
            check((long)paths.ExecuteScalar()! < 20 && (long)metadata.ExecuteScalar()! == 0,
                "compact metadata stores directory paths once and omits repeated full file paths");
            using var ids = InventoryStore.Command(connection, "SELECT id FROM scans WHERE id NOT IN ($a,$b)", ("$a", final.SnapshotId), ("$b", rebased.SnapshotId));
            var remove = new List<long>();
            using (var reader = ids.ExecuteReader()) while (reader.Read()) remove.Add(reader.GetInt64(0));
            new CompactInventory(final.Inventory).Prune(connection, [final.SnapshotId, rebased.SnapshotId], remove);
            check(final.Inventory.Find(final.SnapshotId, file)!.Size == 3000
                && final.Inventory.Find(rebased.SnapshotId, file)!.Size == 2000
                && final.Inventory.Find(final.SnapshotId, Path.Combine(other, "0.dat"))!.Size == 1,
                "history pruning retains shared unchanged rows and both retained snapshot values");
        }

        var newBranch = Path.Combine(root, "created", "nested");
        Directory.CreateDirectory(newBranch);
        var mixed = Path.Combine(newBranch, "Mixed.dat");
        File.WriteAllBytes(mixed, new byte[11]);
        var nested = await scanner.RefreshAsync(final, newBranch, null, default);
        check(nested.Root.Size == final.Root.Size + 11 && nested.DirectoryCount == final.DirectoryCount + 2
            && nested.Root.Children.Any(node => node.Name == "created"),
            "refreshing a new nested folder adds its missing ancestors and updates root totals");
        var folderView = InventoryStore.OpenServiceSnapshot(root, nested.Inventory!.DatabasePath, nested.SnapshotId, newBranch)!;
        var folderCsv = Path.Combine(suite, "folder-view.csv");
        await CsvExporter.ExportAsync(folderView, folderCsv);
        check(folderView.Root.Path == newBranch && folderView.Root.Parent is null && folderView.FileCount == 1
            && folderView.LargestFiles.Single().Path == mixed && File.ReadLines(folderCsv).Count() == 3,
            "a folder opened from a service index keeps its scope in totals, largest files and CSV export");
        check(ServiceProtocol.TryResolveDriveScope(newBranch, out var drive, out var scope) && drive == Path.GetPathRoot(root) && scope == newBranch
            && !ServiceProtocol.TryResolveDriveScope(@"\\server\share\folder", out _, out _)
            && !ServiceProtocol.TryResolveDriveScope(Path.Combine(newBranch, "Mixed.dat:stream"), out _, out _),
            "service scope routing accepts local folders and excludes network paths and alternate streams");
        var uppercase = Path.Combine(newBranch, "MIXED.dat");
        File.Move(mixed, uppercase);
        var renamed = await scanner.RefreshAsync(nested, mixed, null, default);
        check(renamed.Inventory!.Find(renamed.SnapshotId, uppercase)?.Name == "MIXED.dat"
            && nested.Inventory!.Find(nested.SnapshotId, uppercase)?.Name == "Mixed.dat"
            && renamed.Changes.Any(change => change.Path == uppercase && change.Kind == "Changed"),
            "case-only renames retain the actual spelling in the new snapshot, history and changes");
        check(!renamed.Inventory!.FolderUnchanged(newBranch, nested.SnapshotId, renamed.SnapshotId),
            "folder revision detects case-only renames even when totals stay the same");
        File.Delete(uppercase);
        Directory.Delete(newBranch);
        var deleted = await scanner.RefreshAsync(renamed, newBranch, null, default);
        check(!deleted.Inventory!.FolderUnchanged(newBranch, renamed.SnapshotId, deleted.SnapshotId),
            "folder revision detects deleted subtrees through closed version intervals");
        using (var connection = deleted.Inventory!.Open())
        {
            using var ids = InventoryStore.Command(connection, "SELECT id FROM scans WHERE id<>$keep", ("$keep", deleted.SnapshotId));
            var remove = new List<long>();
            using (var reader = ids.ExecuteReader()) while (reader.Read()) remove.Add(reader.GetInt64(0));
            new CompactInventory(deleted.Inventory).Prune(connection, [deleted.SnapshotId], remove);
            using var nodes = InventoryStore.Command(connection, "SELECT count(*) FROM c_nodes WHERE name IN ('Mixed.dat','nested')");
            check((long)nodes.ExecuteScalar()! == 0 && deleted.Root.Size == final.Root.Size,
                "retention purges obsolete catalog nodes after their last snapshot expires");
        }

        var linksRoot = Path.Combine(suite, "compact-hard-links");
        Directory.CreateDirectory(Path.Combine(linksRoot, "a")); Directory.CreateDirectory(Path.Combine(linksRoot, "b"));
        var original = Path.Combine(linksRoot, "a", "file.dat");
        var alias = Path.Combine(linksRoot, "b", "alias.dat");
        File.WriteAllBytes(original, new byte[2000]);
        if (!Native.CreateHardLinkW(alias, original, IntPtr.Zero)) throw new Exception("Cannot create compact hard-link fixture.");
        var linksScanner = new DiskScanner { DatabasePath = Path.Combine(suite, "compact-hard-links.sqlite"), PreferNtfs = false };
        var oldLinks = await linksScanner.ScanAsync(linksRoot, null, default);
        File.WriteAllBytes(original, new byte[8000]);
        var grown = await linksScanner.RefreshAsync(oldLinks, Path.Combine(linksRoot, "a"), null, default);
        check(grown.Root.Size == 16000 && grown.Inventory!.Find(grown.SnapshotId, alias)!.Size == 8000
            && oldLinks.Inventory!.Find(oldLinks.SnapshotId, alias)!.Size == 2000,
            "scoped hard-link growth propagates metadata to aliases outside the changed folder while preserving history");
        File.Delete(original);
        var removed = await linksScanner.RefreshAsync(grown, Path.Combine(linksRoot, "a"), null, default);
        check(removed.Root.Size == 8000 && removed.Root.UniqueAllocatedSize == grown.Root.UniqueAllocatedSize
            && removed.Inventory!.Find(removed.SnapshotId, alias)!.UniqueAllocatedSize == WindowsVolume.ReadEntry(alias).Allocated,
            "removing the canonical link updates ownership in an untouched folder");

        var legacyPath = Path.Combine(suite, "legacy-import.sqlite");
        var legacy = await new DiskScanner { DatabasePath = legacyPath, CompactStorage = false, PreferNtfs = false }.ScanAsync(root, null, default);
        using (var olderSchema = legacy.Inventory!.Open()) InventoryStore.Execute(olderSchema, "ALTER TABLE scans DROP COLUMN timings");
        var before = SHA256.HashData(File.ReadAllBytes(legacyPath));
        var upgrade = new DiskScanner { DatabasePath = legacyPath, PreferNtfs = false };
        var reopened = await upgrade.LoadSavedAsync(root);
        File.WriteAllBytes(file, new byte[4000]);
        var imported = await upgrade.RefreshAsync(reopened!, changed, null, default);
        check(reopened is { Inventory.IsCompact: false } && imported.Inventory!.IsCompact && imported.ScanMethod == "Folder update"
            && imported.Root.Size == 4100 && imported.Work!.VersionsWritten <= 4
            && before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(legacyPath)))
            && legacy.Inventory!.Find(legacy.SnapshotId, file)!.Size == 3000,
            "legacy metadata imports without changing its database or rescanning outside the requested folder");
        VirtualUnknown(suite, check);
        var empty = Path.Combine(suite, "vanishing-root"); Directory.CreateDirectory(empty);
        var emptyDb = Path.Combine(suite, "vanishing-root.sqlite");
        var emptyFirst = await new DiskScanner { DatabasePath = emptyDb, PreferNtfs = false }.ScanAsync(empty, null, default);
        var disappearingRoot = new DiskScanner { DatabasePath = emptyDb, NtfsStageOverride = (_, _, _, _) => { Directory.Delete(empty); return false; } };
        try { await disappearingRoot.ScanAsync(empty, null, default); check(false, "vanished inventory root"); }
        catch (DirectoryNotFoundException)
        { check((await disappearingRoot.LoadSavedAsync(empty))!.SnapshotId == emptyFirst.SnapshotId, "a root that disappears during scanning cannot replace a completed inventory"); }
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var classifier = new CleanupAnalyzer(now);
        var matching = true;
        foreach (var path in new[] { Path.Combine(Path.GetTempPath(), "$Recycle.Bin", "case.TMP"), @"X:\.nuget\packages\case.log", @"X:\case.log",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "case.EXE"), @"X:\ordinary.dat" })
            foreach (var age in new[] { -1d, 0, 1.9, 2, 13.9, 14, 29.9, 30, 119.9, 120, 365 })
            {
                var modified = now.AddDays(-age);
                var reference = new LegacyCleanupAnalyzer(now); reference.Consider(path, 200L * 1024 * 1024, modified);
                var expected = reference.Build();
                var code = CleanupAnalyzer.EligibleCode(classifier.MatchMask(path, 200L * 1024 * 1024), modified.Ticks, now);
                matching &= code == 0 ? expected.Count == 0 : expected.Count == 1 && expected[0].Category == CleanupAnalyzer.FromCode(code, 1, 1, path).Category;
            }
        check(matching, "stored cleanup-rule masks preserve legacy precedence as unchanged files cross age thresholds");
    }
    private static void VirtualUnknown(string suite, Action<bool, string> check)
    {
        var drive = Path.GetPathRoot(suite)!;
        var store = new InventoryStore(drive, Path.Combine(suite, "virtual-compact.sqlite"));
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        InventoryEntry Entry(string name, long size, long? allocated) => new(drive + name, drive, name, false, size, allocated, null, modified, FileAttributes.Normal);
        ScanResult Save(long? basis, string? scope, params InventoryEntry[] entries)
        {
            using var connection = store.Open(); InventoryStore.Execute(connection, "BEGIN IMMEDIATE");
            try
            {
                var id = store.Begin(connection, basis);
                if (scope is not null) store.RemoveSnapshotTree(connection, id, scope);
                using (var writer = store.CreateWriter(connection, id, true))
                    foreach (var entry in entries) writer.Write(entry, entry.IsDirectory ? 0 : 1);
                store.FinishEnumeration(connection, id, default);
                var suggestions = store.AggregateSnapshot(connection, id, default, 0);
                store.CompleteSnapshot(connection, id, "fixture", null, [], default, suggestions);
                InventoryStore.Execute(connection, "COMMIT");
                return store.Load(id);
            }
            catch { InventoryStore.Execute(connection, "ROLLBACK"); throw; }
        }
        var rootEntry = new InventoryEntry(drive, "", drive, true, 0, 0, null, modified, FileAttributes.Directory);
        var first = Save(null, null, rootEntry, Entry("unknown.dat", 10, null), Entry("known.dat", 20, 4096));
        var updated = Save(first.SnapshotId, drive + "unknown.dat", Entry("unknown.dat", 11, 2048));
        check(first.Root.AllocatedSize is null && updated.Root.AllocatedSize == 6144 && updated.Root.UniqueAllocatedSize == 6144
            && store.Enumerate(updated.SnapshotId).Count() == 3 && store.Find(first.SnapshotId, drive)!.AllocatedSize is null,
            "incremental summaries recover from unknown allocation metadata and enumerate drive roots once");
    }
}
