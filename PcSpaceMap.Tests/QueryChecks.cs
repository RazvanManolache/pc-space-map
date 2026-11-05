using System.Text.Json;
using PcSpaceMap.Models;
using PcSpaceMap.Services;

internal static class QueryChecks
{
    internal static async Task RunAsync(string suite, Action<bool, string> check)
    {
        var root = Path.Combine(suite, "query-fixture");
        var scope = Path.Combine(root, "scope%_é😀");
        var sibling = scope + "neighbor";
        Directory.CreateDirectory(Path.Combine(scope, "nested"));
        Directory.CreateDirectory(sibling);
        void Write(string path, int size)
        {
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-180));
        }
        Write(Path.Combine(scope, "old.TMP"), 50);
        Write(Path.Combine(scope, "nested", "small.dat"), 30);
        Write(Path.Combine(sibling, "large.dat"), 900);
        File.SetLastWriteTimeUtc(Path.Combine(sibling, "large.dat"), DateTime.UtcNow.AddDays(-1));
        var database = Path.Combine(suite, "query-fixture.sqlite");
        var result = await new DiskScanner { DatabasePath = database, PreferNtfs = false }.ScanAsync(root, null, default);
        var inventory = result.Inventory!;
        var equivalentCase = scope.Replace("scope", "SCOPE");
        var selected = inventory.Enumerate(result.SnapshotId, equivalentCase).ToList();
        check(selected.Count == 4 && selected.Sum(x => x.IsDirectory ? 0 : x.Size) == 80
            && selected.All(x => !x.Path.StartsWith(sibling, StringComparison.OrdinalIgnoreCase)),
            "indexed subtree browsing preserves Unicode, wildcard characters, ASCII case and neighboring-folder boundaries");
        var largest = inventory.Largest(result.SnapshotId, equivalentCase, 5000);
        check(largest.Count == 2 && largest[0].Size == 50 && largest[1].Size == 30
            && inventory.Largest(result.SnapshotId, limit: 1)[0].Size == 900,
            "subtree and global largest-file queries retain their complete scope");
        check(inventory.Enumerate(result.SnapshotId).Count() == 7
            && inventory.Enumerate(result.SnapshotId, Path.Combine(scope, "old.TMP")).Single().Size == 50
            && !inventory.Enumerate(result.SnapshotId, Path.Combine(root, "absent")).Any(),
            "indexed enumeration handles drive inventory root, individual files and missing paths");
        var reference = new LegacyCleanupAnalyzer();
        foreach (var node in inventory.Enumerate(result.SnapshotId).Where(x => !x.IsDirectory))
            reference.Consider(node.Path, node.Size, node.LastWriteUtc);
        var lowMemory = await new DiskScanner { DatabasePath = Path.Combine(suite, "query-fallback.sqlite"), CompactStorage = false, PreferNtfs = false, SummaryMemoryBudgetBytes = 0 }.ScanAsync(root, null, default);
        string Suggestions(IEnumerable<PcSpaceMap.Models.CleanupSuggestion> value) => JsonSerializer.Serialize(value.OrderBy(x => x.Category));
        check(result.Suggestions.Count > 0 && Suggestions(result.Suggestions) == Suggestions(reference.Build())
            && Suggestions(result.Suggestions) == Suggestions(lowMemory.Suggestions),
            "combined totals and cleanup pass matches legacy cleanup and the low-memory fallback");
        foreach (var view in new[] { result, lowMemory })
        {
            var store = view.Inventory!;
            var scan = view.SnapshotId;
            var cutoff = DateTime.UtcNow.AddDays(-3);
            var recent = new FileFilter("*.dat", 100, null, cutoff);
            var older = new FileFilter("*.dat", 0, cutoff);
            check(store.Largest(scan, root, filter: recent).Single().Name == "large.dat"
                && store.Largest(scan, equivalentCase, filter: recent).Count == 0
                && store.Largest(scan, root, filter: older).Single().Name == "small.dat",
                "largest-file filtering combines modification direction, size, name and subtree scope");
            var pattern = new FileFilter("*.DAT", 0, null);
            var firstPage = store.SearchNames(scan, root, pattern, 0, 1, default);
            var secondPage = store.SearchNames(scan, root, pattern, 1, 1, default);
            var scoped = store.SearchNames(scan, equivalentCase, pattern, 0, 10, default);
            check(firstPage.Count == 1 && secondPage.Count == 1 && firstPage[0].Path != secondPage[0].Path
                && scoped.Single().Path == Path.Combine(scope, "nested", "small.dat"),
                "name search pages through files and respects the selected inventory scope");
            var folderNames = store.SearchNames(scan, root, new FileFilter("SCOPE%_É", 0, null), 0, 10, default);
            var parent = store.Find(scan, scope)!;
            var oldFilter = new FileFilter("old", 0, null);
            var children = store.Children(scan, parent, limit: 1, nameFilter: oldFilter);
            check(folderNames.Count == 2 && folderNames.All(x => x.IsDirectory) && children.Single().Path == Path.Combine(scope, "old.TMP"),
                "name matching includes Unicode folders and filters direct children before paging");
            var candidates = store.FilterCleanup(scan, root, view.CompletedUtc, oldFilter, default);
            check(candidates.Count > 0 && candidates.All(item => item.SamplePath == Path.Combine(scope, "old.TMP")),
                "cleanup name filter calculates groups from matching files and keeps their paths");
        }
        var legacy = await new DiskScanner { CompactStorage = false, DatabasePath = Path.Combine(suite, "legacy-query.sqlite"), PreferNtfs = false }.ScanAsync(root, null, default);
        using var connection = legacy.Inventory!.Open();
        InventoryStore.Execute(connection, "BEGIN");
        try
        {
            var pending = legacy.Inventory.Begin(connection, legacy.SnapshotId);
            InventoryStore.Execute(connection, "INSERT INTO skipped_entries VALUES($id,$path),($id,$neighbor)",
                ("$id", pending), ("$path", Path.Combine(scope, "link")), ("$neighbor", Path.Combine(sibling, "link")));
            InventoryStore.RemoveTree(connection, pending, equivalentCase);
            using var count = InventoryStore.Command(connection, "SELECT count(*) FROM entries WHERE scan=$id", ("$id", pending));
            using var skipped = InventoryStore.Command(connection, "SELECT path FROM skipped_entries WHERE scan=$id", ("$id", pending));
            check((long)count.ExecuteScalar()! == 3 && (string)skipped.ExecuteScalar()! == Path.Combine(sibling, "link"),
                "indexed subtree replacement removes only the selected rows and skipped links");
        }
        finally { InventoryStore.Execute(connection, "ROLLBACK"); }

        // A drive root is already terminated by '\\', so its path equals the
        // descendants' lower bound. It must not appear twice in enumeration.
        var drive = Path.GetPathRoot(root)!;
        var driveInventory = new InventoryStore(drive, Path.Combine(suite, "drive-root-query.sqlite"), true);
        using var driveConnection = driveInventory.Open();
        InventoryStore.Execute(driveConnection, "BEGIN");
        try
        {
            var scan = driveInventory.Begin(driveConnection, null);
            InventoryStore.Execute(driveConnection, """
                INSERT INTO entries(scan,path,parent,name,dir,depth,size,modified,attributes)
                VALUES($id,$root,'',$root,1,0,0,0,16),($id,$file,$root,'probe.dat',0,1,1,0,0);
                """, ("$id", scan), ("$root", drive), ("$file", drive + "probe.dat"));
            InventoryStore.Execute(driveConnection, "COMMIT");
            check(driveInventory.Enumerate(scan).Count() == 2 && driveInventory.Largest(scan).Count == 1,
                "drive-root enumeration includes the root exactly once");
        }
        catch { InventoryStore.Execute(driveConnection, "ROLLBACK"); throw; }
    }
}
