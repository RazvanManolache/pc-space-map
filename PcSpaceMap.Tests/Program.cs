using System.Runtime.InteropServices;
using PcSpaceMap.Services;

if (args.Length >= 2 && args[0] == "--benchmark-compact")
{
    await CompactBenchmark.RunAsync(args[1]);
    return;
}
if (args.Length >= 2 && args[0] == "--benchmark-scan")
{
    await ScanBenchmark.RunAsync(args[1]);
    return;
}
if (args.Length >= 4 && args[0] == "--benchmark-queries")
{
    await QueryBenchmark.RunAsync(args[1], args[2], args[3]);
    return;
}
if (args.Length >= 3 && args[0] == "--benchmark-existing")
{
    await IndexBenchmark.RunAsync(args[1], args[2]);
    return;
}
if (args.Length >= 3 && args[0] == "--benchmark-comparison")
{
    await IndexBenchmark.CompareAsync(args[1], args[2]);
    return;
}

var suite = Path.Combine(Path.GetTempPath(), "pc-space-map-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(suite);
var root = Path.Combine(suite, "files");
Directory.CreateDirectory(root);
var db = Path.Combine(suite, "inventory.sqlite");
var scanner = new DiskScanner { DatabasePath = db, PreferNtfs = false, Parallelism = 4 };
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
    passed++;
}
void Write(string relative, int count)
{
    var path = Path.Combine(root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, new byte[count]);
}
try
{
    SnapshotComparisonChecks.Run(Check);
    WriterChecks.Run(suite, Check);
    ReaderChecks.Run(suite, Check);
    await DiskScanSlotChecks.RunAsync(Check);
    ServiceControlAccessChecks.Run(Check);
    await CompactChecks.RunAsync(suite, Check);
    await NtfsBatchChecks.RunAsync(suite, Check);
    await QueryChecks.RunAsync(suite, Check);
    await MetadataChecks.RunAsync(suite, Check);
    Write("a/one.dat", 100);
    Write("a/deeper/two.dat", 200);
    Write("ab/keep.dat", 300);
    Write("hidden.dat", 400);
    File.SetAttributes(Path.Combine(root, "hidden.dat"), FileAttributes.Hidden);
    Directory.CreateDirectory(Path.Combine(root, "empty"));
    var first = await scanner.ScanAsync(root, null, default);
    Check(first.Root.Size == 1000 && first.FileCount == 4 && first.DirectoryCount == 5, "parallel walk totals, hidden files and empty folders");
    Check(!first.Root.ChildrenLoaded, "inventory opens without materializing its tree");
    Check(first.Root.Children.Count == 4 && first.Root.ChildCount == 4, "lazy direct children and counts");
    Check(first.Inventory!.Find(first.SnapshotId, Path.Combine(root, "a"))!.Size == 300, "bottom-up folder totals");
    var saved = await scanner.LoadSavedAsync(root);
    Check(saved?.SnapshotId == first.SnapshotId && saved.Root.Size == 1000, "saved inventory round trip");

    Write("a/one.dat", 700);
    File.Delete(Path.Combine(root, "a/deeper/two.dat"));
    Write("a/new.dat", 50);
    Write("ab/keep.dat", 900); // Must remain stale outside the explicitly refreshed subtree.
    var scoped = await scanner.RefreshAsync(first, Path.Combine(root, "a"), null, default);
    Check(scoped.Root.Size == 1450 && scoped.FileCount == 4, "folder refresh replaces subtree and recomputes ancestors");
    Check(scoped.Inventory!.Find(scoped.SnapshotId, Path.Combine(root, "ab/keep.dat"))!.Size == 300, "folder refresh respects path boundary");
    Check(scoped.Changes.Any(x => x.Kind == "Deleted" && x.Path.EndsWith("two.dat")) && scoped.Changes.Any(x => x.DeltaBytes == 600 && x.Path.EndsWith("one.dat")), "snapshot diff captures deletion and exact growth");
    Check(first.Inventory!.Find(first.SnapshotId, Path.Combine(root, "a/one.dat"))!.Size == 100, "previous snapshot remains immutable");

    using (var cancel = new CancellationTokenSource())
    {
        cancel.Cancel();
        try { await scanner.ScanAsync(root, null, cancel.Token); throw new Exception("Cancellation was ignored"); }
        catch (OperationCanceledException) { }
    }
    Check((await scanner.LoadSavedAsync(root))!.SnapshotId == scoped.SnapshotId, "cancelled scan preserves completed snapshot");
    var csv = Path.Combine(suite, "export.csv");
    await CsvExporter.ExportAsync(scoped, csv);
    Check(File.ReadLines(csv).Count() == 10 && File.ReadAllText(csv).Contains("a\\new.dat"), "export streams complete inventory including unopened folders");

    File.Move(Path.Combine(root, "a/new.dat"), Path.Combine(root, "a/renamed.dat"));
    var update = await scanner.RefreshAsync(scoped, null, null, default);
    Check(update.Root.Size == 2050 && update.Inventory!.Find(update.SnapshotId, Path.Combine(root, "a/renamed.dat")) is not null
        && update.Inventory.Find(update.SnapshotId, Path.Combine(root, "a/new.dat")) is null, "change update or full-scan fallback converges after rename");
    Console.WriteLine("Update method: " + update.ScanMethod);

    for (var i = 0; i < 1250; i++) Write("many/" + i.ToString("D4") + ".dat", 10);
    var wide = await scanner.ScanAsync(root, null, default);
    var many = wide.Inventory!.Find(wide.SnapshotId, Path.Combine(root, "many"))!;
    Check(many.Children.Count == 350 && many.ChildCount == 1250 && wide.Inventory.Children(wide.SnapshotId, many, 1200, 200).Count == 50, "wide directories stay bounded and all entries are pageable");
    Check(wide.Inventory.Largest(wide.SnapshotId, Path.Combine(root, "many"), 5).Count == 5, "largest files can query any subtree");

    var link = Path.Combine(root, "alias.dat");
    if (Native.CreateHardLinkW(link, Path.Combine(root, "hidden.dat"), IntPtr.Zero))
    {
        var hard = await scanner.ScanAsync(root, null, default);
        Check(hard.Root.Size == wide.Root.Size + 400, "logical accounting includes hard-link aliases");
        Check(hard.Root.UniqueAllocatedSize == wide.Root.UniqueAllocatedSize, "physical accounting counts hard-linked storage once");
    }
    else throw new Exception("Cannot create hard-link fixture: " + Marshal.GetLastWin32Error());
    var aliases = WindowsVolume.HardLinkPaths(link).ToArray();
    Check(aliases.Contains(link, StringComparer.OrdinalIgnoreCase) && aliases.Contains(Path.Combine(root, "hidden.dat"), StringComparer.OrdinalIgnoreCase), "NTFS alias enumeration returns every hard-link pathname");
    var disappearing = Path.Combine(root, "disappearing.dat");
    var survivor = Path.Combine(root, "surviving-alias.dat");
    File.WriteAllBytes(disappearing, new byte[8]);
    if (!Native.CreateHardLinkW(survivor, disappearing, IntPtr.Zero)) throw new Exception("Cannot create race fixture.");
    var beforeDisappearance = WindowsVolume.ReadEntry(disappearing);
    File.Delete(disappearing);
    var raceIssues = new List<PcSpaceMap.Models.ScanIssue>();
    DiskScanner.ExpandHardLinks(beforeDisappearance, root, _ => throw new Exception("Unexpected alias"), raceIssues.Add, default);
    Check(raceIssues.Count == 1, "a disappearing hard-link source is recorded without aborting the scan");
    File.Delete(survivor);
    var beforeAliasDelete = (await scanner.LoadSavedAsync(root))!;
    File.Delete(link);
    var afterAliasDelete = await scanner.RefreshAsync(beforeAliasDelete, root, null, default);
    Check(afterAliasDelete.Root.UniqueAllocatedSize == beforeAliasDelete.Root.UniqueAllocatedSize, "removing the canonical hard-link alias reassigns unique allocation correctly");

    var fallback = await new DiskScanner { DatabasePath = Path.Combine(suite, "fallback.sqlite"), CompactStorage = false, PreferNtfs = false, SummaryMemoryBudgetBytes = 0 }.ScanAsync(root, null, default);
    Check(fallback.Root.Size == afterAliasDelete.Root.Size && fallback.Root.UniqueAllocatedSize == afterAliasDelete.Root.UniqueAllocatedSize
        && fallback.FileCount == afterAliasDelete.FileCount, "bounded-memory SQL fallback matches compact folder aggregation");
    var lockedPath = Path.Combine(root, "locked.dat");
    using (var locked = new FileStream(lockedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        locked.Write(new byte[32]);
        locked.Flush();
        var metadata = WindowsVolume.ReadEntry(lockedPath, attributesOnly: true);
        Check(metadata.Size == 32 && metadata.Allocated is null, "attribute-only fallback retains logical metadata with unknown allocation");
    }
    var reservedPath = @"\\?\" + Path.Combine(root, "nul");
    File.WriteAllBytes(reservedPath, new byte[7]);
    try
    {
        Check(WindowsVolume.ReadEntry(Path.Combine(root, "nul")).Size == 7, "extended native paths read literal DOS-reserved filenames");
        Check(InventoryStore.Normalize(Path.Combine(root, "nul")) == Path.Combine(root, "nul"), "inventory paths preserve literal reserved filenames");
        var literal = await scanner.ScanAsync(root, null, default);
        Check(literal.Inventory!.Find(literal.SnapshotId, Path.Combine(root, "nul"))?.Size == 7, "literal reserved files remain navigable in the saved index");
        File.WriteAllBytes(reservedPath, new byte[17]);
        var literalUpdated = await scanner.RefreshAsync(literal, Path.Combine(root, "nul"), null, default);
        Check(literalUpdated.Root.Size == literal.Root.Size + 10
            && literalUpdated.Inventory!.Find(literalUpdated.SnapshotId, Path.Combine(root, "nul"))?.Size == 17,
            "a scoped refresh reads a literal DOS-reserved file by its NTFS name");
    }
    finally { File.Delete(reservedPath); }

    var sparsePath = Path.Combine(root, "sparse.dat");
    using (var sparse = new FileStream(sparsePath, FileMode.CreateNew, FileAccess.ReadWrite))
    {
        Check(Native.DeviceIoControl(sparse.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero), "create sparse fixture");
        sparse.SetLength(64 * 1024 * 1024);
        sparse.Write(new byte[4096]);
    }
    var sparseInfo = WindowsVolume.ReadEntry(sparsePath);
    Check(sparseInfo.Size == 64 * 1024 * 1024 && sparseInfo.Allocated is > 0 and < 64 * 1024 * 1024, "sparse allocated space differs from logical length");

    var record = new byte[80];
    BitConverter.GetBytes(72).CopyTo(record, 8);
    BitConverter.GetBytes((ushort)2).CopyTo(record, 12);
    BitConverter.GetBytes(42UL).CopyTo(record, 16);
    BitConverter.GetBytes(5UL).CopyTo(record, 24);
    BitConverter.GetBytes(0x100U).CopyTo(record, 48);
    BitConverter.GetBytes((ushort)8).CopyTo(record, 64);
    BitConverter.GetBytes((ushort)60).CopyTo(record, 66);
    System.Text.Encoding.Unicode.GetBytes("test").CopyTo(record, 68);
    var decoded = WindowsVolume.Parse(record, 80, true).Single();
    Check(decoded.FileId == 42 && decoded.ParentId == 5 && decoded.Name == "test" && decoded.Reason == 0x100, "decode NTFS journal record fields");
    BitConverter.GetBytes((ushort)3).CopyTo(record, 12);
    try { WindowsVolume.Parse(record, 80).ToArray(); throw new Exception("Invalid record accepted"); }
    catch (IOException) { Check(true, "unsupported NTFS records trigger scan fallback"); }

    using (var connection = wide.Inventory.Open())
    {
        InventoryStore.Execute(connection, "CREATE TEMP TABLE mft(id INTEGER PRIMARY KEY,parent INTEGER,name TEXT,attributes INTEGER); INSERT INTO mft VALUES(5,5,'root',16),(6,5,'folder',16),(7,6,'file.dat',0),(8,5,'junction',1040),(9,8,'skip.dat',0);");
        var mftPaths = WindowsVolume.MftPaths(connection, root).ToArray();
        Check(mftPaths.Contains(Path.Combine(root, "folder", "file.dat")) && !mftPaths.Any(x => x.EndsWith("skip.dat")), "NTFS hierarchy resolves parent IDs and excludes reparse subtrees");
        InventoryStore.Execute(connection, "DELETE FROM mft WHERE id=5");
        var withoutRoot = WindowsVolume.MftPaths(connection, root).ToArray();
        Check(withoutRoot.SequenceEqual(mftPaths), "NTFS paths resolve when enumeration omits the volume root record");
    }
    try
    {
        WindowsVolume.Changes(new("C:\\", "old", 1, 5, 0), new("C:\\", "new", 1, 10, 0), default);
        throw new Exception("Changed volume accepted");
    }
    catch (IOException) { Check(true, "replaced volumes invalidate journal cursors"); }

    var journalRoot = Path.Combine(root, "journal");
    Directory.CreateDirectory(journalRoot);
    File.WriteAllBytes(Path.Combine(journalRoot, "grow.dat"), new byte[10]);
    File.WriteAllBytes(Path.Combine(journalRoot, "delete.dat"), new byte[20]);
    ulong Id(string path) => Convert.ToUInt64(WindowsVolume.ReadEntry(path).Identity!.Split(':')[1], 16);
    var serial = WindowsVolume.ReadEntry(journalRoot).Identity!.Split(':')[0];
    var parentId = Id(journalRoot);
    var growId = Id(Path.Combine(journalRoot, "grow.dat"));
    var deleteId = Id(Path.Combine(journalRoot, "delete.dat"));
    var journal = new TestJournal { Head = new(Path.GetPathRoot(journalRoot)!, serial, 7, 10, 0) };
    var journalScanner = new DiskScanner { DatabasePath = Path.Combine(suite, "journal.sqlite"), PreferNtfs = false, Journal = journal };
    var journalFirst = await journalScanner.ScanAsync(journalRoot, null, default);
    File.WriteAllBytes(Path.Combine(journalRoot, "grow.dat"), new byte[100]);
    File.Delete(Path.Combine(journalRoot, "delete.dat"));
    File.WriteAllBytes(Path.Combine(journalRoot, "added.dat"), new byte[30]);
    var addedId = Id(Path.Combine(journalRoot, "added.dat"));
    journal.Head = journal.Head with { NextUsn = 20 };
    journal.Events = [new(growId, parentId, 1, "grow.dat"), new(deleteId, parentId, 0x200, "delete.dat"), new(addedId, parentId, 0x100, "added.dat")];
    journal.Paths[growId] = Path.Combine(journalRoot, "grow.dat");
    journal.Paths[addedId] = Path.Combine(journalRoot, "added.dat");
    var journalUpdated = await journalScanner.RefreshAsync(journalFirst, null, null, default);
    Check(journalUpdated.ScanMethod == "NTFS journal update" && journalUpdated.Root.Size == 130 && journalUpdated.FileCount == 2,
        "journal-only updates apply creation, growth and deletion with exact ancestor totals");
    File.Move(Path.Combine(journalRoot, "added.dat"), Path.Combine(journalRoot, "renamed.dat"));
    journal.Head = journal.Head with { NextUsn = 30 };
    journal.Events = [new(addedId, parentId, 0x1000, "added.dat"), new(addedId, parentId, 0x2000, "renamed.dat")];
    journal.Paths[addedId] = Path.Combine(journalRoot, "renamed.dat");
    var journalRenamed = await journalScanner.RefreshAsync(journalUpdated, null, null, default);
    Check(journalRenamed.Inventory!.Find(journalRenamed.SnapshotId, Path.Combine(journalRoot, "added.dat")) is null
        && journalRenamed.Inventory.Find(journalRenamed.SnapshotId, Path.Combine(journalRoot, "renamed.dat"))!.Size == 30, "journal rename removes old pathname and inserts new pathname");
    journal.Events = [];
    var noChanges = await journalScanner.RefreshAsync(journalRenamed, null, null, default);
    Check(noChanges.Root.Size == 130 && noChanges.Changes.Count == 0, "empty journal update preserves inventory");
    journal.Fail = true;
    File.WriteAllBytes(Path.Combine(journalRoot, "missed.dat"), new byte[40]);
    var journalOnly = new DiskScanner { PreferNtfs = false, Journal = journal, RequireJournalForDriveRefresh = true };
    var automaticPhases = new List<string>();
    try
    {
        await journalOnly.RefreshAsync(noChanges, null, new InlineProgress(p => automaticPhases.Add(p.Phase)), default);
        throw new InvalidOperationException("A journal-only refresh silently rebuilt the index.");
    }
    catch (JournalRefreshException ex)
    {
        Check(ex.LogReason == "Simulated journal wrap" && noChanges.Inventory!.LatestId() == noChanges.SnapshotId
            && !automaticPhases.Contains("Enumerating NTFS names"),
            "journal-only refresh leaves the saved snapshot intact when the journal cannot be replayed");
    }
    var recovered = await journalScanner.RefreshAsync(noChanges, null, null, default);
    Check(recovered.Root.Size == 170 && recovered.ScanMethod == "Parallel directory scan" && recovered.Issues.Any(), "lost journal falls back to a complete inventory");
    Check(recovered.Timings.Any(x => x.Phase == "Folder totals and hard links") && (await journalScanner.LoadSavedAsync(journalRoot))!.Timings.Count > 0, "phase timings are returned and saved");

    // Use the fixture as a simulated volume root so the NTFS-only destination can be reproduced safely.
    var pendingDeletes = Path.Combine(journalRoot, "$Extend", "$Deleted");
    Directory.CreateDirectory(pendingDeletes);
    var unlinkedPath = Path.Combine(pendingDeletes, "pending.dat");
    File.Move(Path.Combine(journalRoot, "renamed.dat"), unlinkedPath);
    journal.Fail = false;
    journal.Head = journal.Head with { Volume = journalRoot, NextUsn = 40 };
    journal.Events = [new(addedId, parentId, 0x200, "renamed.dat")];
    journal.Paths[addedId] = unlinkedPath;
    var unlinked = await journalScanner.RefreshAsync(recovered, null, null, default);
    Check(unlinked.ScanMethod == "NTFS journal update" && unlinked.Root.Size == 140
        && unlinked.Inventory!.Find(unlinked.SnapshotId, Path.Combine(journalRoot, "renamed.dat")) is null
        && unlinked.Inventory.Find(unlinked.SnapshotId, unlinkedPath) is null,
        "pending NTFS deletions remove the previous indexed name without indexing the internal path");

    // Cancellation after enumeration started must roll back the entire new generation.
    using (var cancel = new CancellationTokenSource())
    {
        var before = (await scanner.LoadSavedAsync(root))!.SnapshotId;
        try
        {
            await scanner.ScanAsync(root, new InlineProgress(p => { if (p.FileCount > 0) cancel.Cancel(); }), cancel.Token);
            throw new Exception("Mid-scan cancellation was ignored");
        }
        catch (OperationCanceledException) { }
        Check((await scanner.LoadSavedAsync(root))!.SnapshotId == before, "mid-scan cancellation rolls back pending inventory");
    }
    if (args.Contains("--stress"))
    {
        var expected = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) + 20_000;
        for (var i = 0; i < 20_000; i++) Write($"stress/bucket-{i / 200}/{i:D5}.dat", 1);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stress = await scanner.ScanAsync(root, null, default);
        var scanSeconds = clock.Elapsed.TotalSeconds;
        Check(stress.Root.Size == expected && !stress.Root.ChildrenLoaded, "20,000-file scan retains exact totals and lazy navigation");
        clock.Restart();
        var reopened = await scanner.LoadSavedAsync(root);
        Check(reopened!.SnapshotId == stress.SnapshotId, "large inventory reopens from saved summaries");
        Console.WriteLine($"STRESS: {stress.FileCount:N0} files; scan {scanSeconds:F2}s; reopen {clock.Elapsed.TotalMilliseconds:F0}ms; process peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F1} MB");
    }
    Console.WriteLine($"{passed} checks passed.");
}
finally
{
    // This is a uniquely created fixture directory, never a user-data path.
    var fixtureParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    if (Path.GetDirectoryName(Path.GetFullPath(suite)) != fixtureParent || !Path.GetFileName(suite).StartsWith("pc-space-map-tests-"))
        throw new InvalidOperationException("Unexpected fixture cleanup path.");
    Directory.Delete(suite, true);
}

sealed class InlineProgress(Action<PcSpaceMap.Models.ScanProgress> report) : IProgress<PcSpaceMap.Models.ScanProgress>
{
    public void Report(PcSpaceMap.Models.ScanProgress value) => report(value);
}
static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}

sealed class TestJournal : IVolumeJournal
{
    public required JournalCursor Head { get; set; }
    public List<VolumeChange> Events { get; set; } = [];
    public Dictionary<ulong, string> Paths { get; } = [];
    public bool Fail { get; set; }
    public JournalCursor? Cursor(string root) => Head;
    public IReadOnlyList<VolumeChange> Changes(JournalCursor saved, JournalCursor head, CancellationToken token) =>
        Fail ? throw new IOException("Simulated journal wrap") : Events;
    public string? ResolveId(string volume, ulong fileId) => Paths.GetValueOrDefault(fileId);
}
