using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

public sealed class DiskScanner
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);
    public bool AutoTuneReaders { get; init; }
    internal ReaderTuner Tuner { get; init; } = new();
    internal Func<Microsoft.Data.Sqlite.SqliteConnection, string, CancellationToken, IProgress<ScanProgress>?, bool>? NtfsStageOverride { get; init; }
    public bool PreferNtfs { get; init; } = true;
    internal bool BatchDirectoryMetadata { get; init; } = true;
    internal bool DirectInventoryWrites { get; init; } = true;
    internal bool CompactStorage { get; init; } = true;
    public string? DatabasePath { get; init; }
    internal string? ExcludedDirectory { get; init; }
    internal TimeSpan HistoryGrace { get; init; }
    internal uint? RequiredVolumeSerial { get; init; }
    internal bool RequireJournalForDriveRefresh { get; init; }
    public long SummaryMemoryBudgetBytes { get; init; } = 768L * 1024 * 1024;
    internal IVolumeJournal Journal { get; init; } = new WindowsJournal();

    public Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken token) =>
        ScanAsync(rootPath, progress, token, null);

    internal async Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken token, string? initialNote)
    {
        var store = StoreFor(rootPath);
        var imported = await EnsureBaseline(store, progress, token);
        var result = await RunAsync(store, null, null, false, progress, token, initialNote);
        if (imported > TimeSpan.Zero) { result.IncludePhase("Import saved metadata", imported); store.SaveTimings(result.SnapshotId, result.Timings); }
        return result;
    }

    public async Task<ScanResult> RefreshAsync(ScanResult result, string? folder, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var store = result.Inventory ?? throw new InvalidOperationException("Inventory is not indexed.");
        if (folder is not null && !IsBelow(folder, result.Root.Path)) throw new ArgumentException("Folder is outside this inventory.");
        if (CompactStorage && !store.IsCompact)
        {
            var target = StoreFor(result.Root.Path);
            var imported = await EnsureBaseline(target, progress, token, store.DatabasePath);
            var basis = target.LatestId() ?? throw new InvalidOperationException("The saved inventory could not be imported.");
            var updated = await RunAsync(target, basis, folder, folder is null, progress, token,
                "The older inventory was preserved. Updates now use a compact baseline imported from saved metadata.");
            if (imported > TimeSpan.Zero) { updated.IncludePhase("Import saved metadata", imported); target.SaveTimings(updated.SnapshotId, updated.Timings); }
            return updated;
        }
        return await RunAsync(store, result.SnapshotId, folder, folder is null, progress, token);
    }

    private async Task<TimeSpan> EnsureBaseline(InventoryStore store, IProgress<ScanProgress>? progress, CancellationToken token, string? source = null)
    {
        if (!store.IsCompact || store.LatestId().HasValue) return TimeSpan.Zero;
        source ??= DatabasePath ?? store.DatabasePath[..^".v3.sqlite".Length] + ".sqlite";
        if (!LegacyFile(source)) return TimeSpan.Zero;
        var gate = Gates.GetOrAdd(store.DatabasePath, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(token);
        try
        {
            var clock = Stopwatch.StartNew();
            var imported = await Task.Run(() => CompactImport.Import(store, source, progress, token), token);
            return imported ? clock.Elapsed : TimeSpan.Zero;
        }
        finally { gate.Release(); }
    }

    private static bool LegacyFile(string path)
    {
        if (!File.Exists(path)) return false;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = InventoryStore.Command(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='entries'");
        return (long)command.ExecuteScalar()! != 0;
    }
    private InventoryStore StoreFor(string root)
    {
        var path = DatabasePath;
        if (CompactStorage && path is not null && LegacyFile(path)) path += ".v3.sqlite";
        return new InventoryStore(root, path, !CompactStorage) { HistoryGrace = HistoryGrace };
    }

    public async Task<ScanResult?> LoadSavedAsync(string rootPath)
    {
        var store = StoreFor(rootPath);
        var gate = Gates.GetOrAdd(store.DatabasePath, _ => new SemaphoreSlim(1));
        await gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (store.LatestId() is { } id) return store.Load(id);
                if (CompactStorage)
                {
                    var legacyPath = DatabasePath ?? store.DatabasePath[..^".v3.sqlite".Length] + ".sqlite";
                    if (LegacyFile(legacyPath))
                    {
                        var legacy = new InventoryStore(rootPath, legacyPath, true, true);
                        if (legacy.LatestId() is { } old) return legacy.Load(old);
                    }
                }
                return null;
            });
        }
        finally { gate.Release(); }
    }

    private async Task<ScanResult> RunAsync(InventoryStore store, long? basis, string? folder, bool incremental,
        IProgress<ScanProgress>? progress, CancellationToken token, string? initialNote = null)
    {
        var gate = Gates.GetOrAdd(store.DatabasePath, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(token);
        try { return await Task.Run(() => Run(store, basis, folder, incremental, progress, token, initialNote), token); }
        finally { gate.Release(); }
    }

    private ScanResult Run(InventoryStore store, long? basis, string? folder, bool incremental,
        IProgress<ScanProgress>? progress, CancellationToken token, string? initialNote)
    {
        var timer = Stopwatch.StartNew();
        long files = 0, dirs = 0, bytes = 0;
        var timings = new List<ScanPhaseTiming>();
        var phaseStart = TimeSpan.Zero;
        void FinishPhase(string phase)
        {
            var now = timer.Elapsed;
            timings.Add(new(phase, (now - phaseStart).TotalSeconds));
            phaseStart = now;
        }
        string phase = "";
        long completedUnits = 0;
        long? totalUnits = null;
        var lastProgress = TimeSpan.Zero;
        void Report(string path, bool force = false)
        {
            if (progress is null || (!force && timer.Elapsed - lastProgress < TimeSpan.FromMilliseconds(300))) return;
            lastProgress = timer.Elapsed;
            progress.Report(new() { Phase = phase, CurrentPath = path, FileCount = files, DirectoryCount = dirs,
                BytesFound = bytes, CompletedUnits = totalUnits.HasValue ? completedUnits : null, TotalUnits = totalUnits });
        }
        void Phase(string name, long? total = null)
        {
            phase = name;
            completedUnits = 0;
            totalUnits = total;
            Report(name, force: true);
        }
        var root = store.RootPath;
        if (!WindowsVolume.ReadEntry(root, verifyPath: !root.StartsWith(@"\\") || RequiredVolumeSerial.HasValue,
                requiredSerial: RequiredVolumeSerial).IsDirectory) throw new DirectoryNotFoundException(root);
        var inventoryDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCSpaceMap", "inventories");
        bool Excluded(string path) => path.Equals(inventoryDirectory, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(inventoryDirectory + "\\", StringComparison.OrdinalIgnoreCase)
            || (ExcludedDirectory is not null && IsBelow(path, ExcludedDirectory))
            || path.Equals(store.DatabasePath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(store.DatabasePath + "-wal", StringComparison.OrdinalIgnoreCase) || path.Equals(store.DatabasePath + "-shm", StringComparison.OrdinalIgnoreCase);
        if (Excluded(root)) throw new ArgumentException("The scanner's own inventory storage is excluded from inventories.");
        if (folder is not null && !IsBelow(folder, root)) throw new ArgumentException("Folder is outside this inventory.");
        if (folder is not null) VerifyScopeAncestors(InventoryStore.Normalize(folder), root);
        // Capture before enumeration: changes during scanning must remain eligible at the next update.
        var head = Journal.Cursor(root);
        var selection = new ReaderSelection(Math.Clamp(Parallelism, 1, 16), false, false);
        if (AutoTuneReaders && !RequiredVolumeSerial.HasValue)
        {
            Phase("Choosing readers for this drive");
            try { selection = Tuner.Select(root, Parallelism, token, Excluded); }
            catch (Exception ex) when (Expected(ex)) { selection = new(Math.Clamp(Parallelism, 1, 16), false, false); }
        }
        var readers = selection.Readers;
        store.SetReaderSelection(selection);
        FinishPhase("Reader selection");
        var scopes = new List<string>();
        var method = "Parallel directory scan";
        var notes = new ConcurrentQueue<ScanIssue>();
        if (initialNote is not null) notes.Enqueue(new() { Path = root, Message = initialNote });
        if (store.IsCompact && basis.HasValue && basis != store.LatestId())
        {
            basis = store.LatestId(); head = Journal.Cursor(root);
            notes.Enqueue(new() { Path = root, Message = "Updates are applied to the latest saved inventory. The older open view remains unchanged." });
        }
        var copied = basis.HasValue && folder is not null;
        if (folder is not null) scopes.Add(InventoryStore.Normalize(folder));
        else if (incremental && basis.HasValue && store.ReadJournal(basis.Value) is { } saved && head is not null)
        {
            try
            {
                scopes = ChangedPaths(store, basis.Value, saved, head, token);
                copied = true;
                method = "NTFS journal update";
            }
            catch (Exception ex) when (ex is (IOException or System.ComponentModel.Win32Exception) and not JournalRefreshException)
            {
                if (RequireJournalForDriveRefresh) throw new JournalRefreshException(ex.Message, ex);
                notes.Enqueue(new() { Path = root, Message = ex.Message + " Running a full scan." });
            }
        }
        else if (incremental)
        {
            const string message = "Change journal access unavailable.";
            if (RequireJournalForDriveRefresh) throw new JournalRefreshException(message, logReason: message);
            notes.Enqueue(new() { Path = root, Message = message + " Running a full scan." });
        }
        if (!copied) scopes = [root];
        using var connection = store.Open();
        FinishPhase("Discover changes");
        InventoryStore.Execute(connection, "BEGIN IMMEDIATE");
        long id;
        long skipped = 0;
        try
        {
            if (store.IsCompact && copied)
            {
                using var latest = InventoryStore.Command(connection, "SELECT max(id) FROM scans WHERE completed IS NOT NULL");
                if (latest.ExecuteScalar() as long? != basis)
                {
                    basis = latest.ExecuteScalar() as long?;
                    notes.Enqueue(new() { Path = root, Message = "Another process updated the saved inventory. Updating its latest baseline while preserving older snapshots." });
                    if (folder is null)
                    {
                        head = Journal.Cursor(root);
                        try
                        {
                            var saved = basis.HasValue ? store.ReadJournal(basis.Value) : null;
                            if (saved is null || head is null)
                            {
                                const string message = "Change journal access unavailable.";
                                if (RequireJournalForDriveRefresh) throw new JournalRefreshException(message, logReason: message);
                                throw new IOException(message);
                            }
                            scopes = ChangedPaths(store, basis!.Value, saved, head, token);
                        }
                        catch (Exception ex) when (ex is (IOException or System.ComponentModel.Win32Exception) and not JournalRefreshException)
                        {
                            if (RequireJournalForDriveRefresh) throw new JournalRefreshException(ex.Message, ex);
                            copied = false; basis = null; scopes = [root]; method = "Parallel directory scan";
                        }
                    }
                }
            }
            var minimal = MinimizeScopes(scopes.Where(path => !Excluded(path)).Select(path =>
                copied ? PromoteScope(store, connection, basis!.Value, path) : path), token);
            id = store.Begin(connection, copied ? basis : null);
            if (copied)
            {
                var old = store.Load(basis!.Value);
                skipped = old.SkippedReparsePoints;
                foreach (var issue in old.Issues.Where(x => !minimal.Any(p => IsBelow(x.Path, p)))) notes.Enqueue(issue);
                foreach (var scope in minimal) store.RemoveSnapshotTree(connection, id, scope);
                if (folder is not null) method = "Folder update";
            }
            FinishPhase("Prepare inventory");
            using var insert = store.CreateWriter(connection, id, DirectInventoryWrites);
            var rootPrefixLength = root.TrimEnd('\\').Length + 1;
            void Consume(InventoryEntry entry)
            {
                token.ThrowIfCancellationRequested();
                if (Excluded(entry.Path)) return;
                var depth = entry.Path.Equals(root, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                if (depth > 0) foreach (var character in entry.Path.AsSpan(rootPrefixLength)) if (character == '\\') depth++;
                var skippedEntry = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                insert.Write(entry, depth, skippedEntry);
                if (skippedEntry) return;
                if (entry.IsDirectory) dirs++; else { files++; bytes += entry.Size; }
                if ((files + dirs) % 1000 == 0) Report(entry.Path, force: !totalUnits.HasValue);
            }
            var fast = false;
            if (!copied && PreferNtfs)
            {
                Phase("Enumerating NTFS names");
                try { fast = NtfsStageOverride is null ? WindowsVolume.StageMft(connection, root, token, progress) : NtfsStageOverride(connection, root, token, progress); }
                catch (IOException ex)
                {
                    InventoryStore.Execute(connection, "DROP TABLE IF EXISTS mft");
                    notes.Enqueue(new() { Path = root, Message = ex.Message + " Using directory scanning." });
                }
            }
            FinishPhase("Enumerate NTFS names");
            Phase("Metadata and persistence");
            if (fast)
            {
                method = "NTFS metadata scan";
                if (BatchDirectoryMetadata)
                {
                    method = "NTFS batched metadata scan";
                    if (progress is not null)
                    {
                        Phase("Counting staged folders");
                        var folderCount = WindowsVolume.CountMftDirectories(connection);
                        token.ThrowIfCancellationRequested();
                        Phase("Reading folder metadata", folderCount);
                    }
                    ReadDirectoriesParallel(WindowsVolume.MftPaths(connection, root, true, true), Consume, notes, token, Excluded, readers,
                        (count, path) =>
                        {
                            completedUnits += count;
                            Report(path, force: completedUnits == totalUnits);
                        });
                }
                else
                {
                    Consume(WindowsVolume.ReadEntry(root, verifyPath: true, requiredSerial: RequiredVolumeSerial));
                    ReadPathsParallel(WindowsVolume.MftPaths(connection, root).Where(path => !Excluded(path)), entry =>
                    {
                        Consume(entry);
                        ExpandHardLinks(entry, root, Consume, issue => notes.Enqueue(issue), token, RequiredVolumeSerial);
                    }, notes, token, readers);
                }
                if (files == 0 && dirs <= 1 && Directory.EnumerateFileSystemEntries(root).Any())
                    throw new IOException("NTFS enumeration returned no entries for a populated volume; the inventory was not saved.");
            }
            else
            {
                if (copied && minimal.Count > 1) Phase("Refreshing changed paths", minimal.Count);
                foreach (var scope in minimal)
                {
                    try
                    {
                        var entry = WindowsVolume.ReadEntry(scope, verifyPath: copied || !scope.StartsWith(@"\\"), requiredSerial: RequiredVolumeSerial);
                        if (entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                            Walk(entry, Consume, notes, token, Excluded, readers);
                        else Consume(entry);
                    }
                    catch (FileNotFoundException ex)
                    {
                        if (scope.Equals(root, StringComparison.OrdinalIgnoreCase))
                            throw new DirectoryNotFoundException("The inventory root disappeared during the scan.", ex);
                    }
                    catch (Exception ex) when (!scope.Equals(root, StringComparison.OrdinalIgnoreCase) && Expected(ex))
                    { notes.Enqueue(new() { Path = scope, Message = ex.Message }); }
                    if (totalUnits.HasValue)
                    {
                        completedUnits++;
                        Report(scope, force: completedUnits == totalUnits);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            store.FinishEnumeration(connection, id, token);
            FinishPhase("Metadata and persistence");
            Phase("Calculate folder totals and hard links");
            var suggestions = store.AggregateSnapshot(connection, id, token, SummaryMemoryBudgetBytes);
            FinishPhase("Folder totals and hard links");
            var cursor = folder is not null && basis.HasValue ? store.ReadJournal(basis.Value) : head;
            Phase("Cleanup review and comparison");
            store.CompleteSnapshot(connection, id, method, cursor, notes.ToArray(), token, suggestions);
            FinishPhase("Cleanup review and comparison");
            token.ThrowIfCancellationRequested();
            if (RequiredVolumeSerial.HasValue)
                WindowsVolume.ReadEntry(root, verifyPath: true, requiredSerial: RequiredVolumeSerial);
            InventoryStore.Execute(connection, "COMMIT");
            FinishPhase("Commit");
        }
        catch { InventoryStore.Execute(connection, "ROLLBACK"); throw; }
        Phase("Open completed inventory");
        var result = store.Load(id, timer.Elapsed, timings);
        FinishPhase("Open inventory");
        store.PruneHistory();
        FinishPhase("History retention");
        store.SaveTimings(id, timings);
        result.SetDuration(timer.Elapsed);
        return result;
    }

    private void ReadDirectoriesParallel(IEnumerable<string> paths, Action<InventoryEntry> consume, ConcurrentQueue<ScanIssue> issues,
        CancellationToken token, Func<string, bool> excluded, int readers, Action<int, string> batchCompleted)
    {
        // Pause the SQLite name reader between batches. Only this thread writes
        // inventory rows; filesystem workers never touch the SQLite connection.
        foreach (var batch in paths.Chunk(256))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            var output = Channel.CreateBounded<InventoryEntry>(4096);
            var workers = Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = readers, CancellationToken = linked.Token }, async (path, ct) =>
            {
                if (excluded(path)) return;
                try
                {
                    foreach (var entry in DirectoryEntries(path, issues, ct, excluded))
                        if (!excluded(entry.Path) && (!entry.IsDirectory || entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                            || (entry.Attributes & FileAttributes.ReparsePoint) != 0)) await output.Writer.WriteAsync(entry, ct);
                }
                catch (Exception ex) when (Expected(ex)) { issues.Enqueue(new() { Path = path, Message = ex.Message }); }
            });
            _ = workers.ContinueWith(task => output.Writer.TryComplete(task.Exception), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                while (output.Reader.WaitToReadAsync(linked.Token).AsTask().GetAwaiter().GetResult())
                    while (output.Reader.TryRead(out var entry)) consume(entry);
                workers.GetAwaiter().GetResult();
                batchCompleted(batch.Length, batch[^1]);
            }
            finally
            {
                linked.Cancel();
                try { workers.GetAwaiter().GetResult(); } catch when (linked.IsCancellationRequested) { }
            }
        }
    }

    private void Walk(InventoryEntry root, Action<InventoryEntry> consume, ConcurrentQueue<ScanIssue> issues, CancellationToken token, Func<string, bool> excluded, int readers)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = linked.Token;
        var directories = Channel.CreateUnbounded<InventoryEntry>();
        var output = Channel.CreateBounded<InventoryEntry>(4096);
        long pending = 1;
        directories.Writer.TryWrite(root);
        var workers = Enumerable.Range(0, readers).Select(_ => Task.Run(async () =>
        {
            try
            {
                await foreach (var node in directories.Reader.ReadAllAsync(ct))
                {
                    var path = node.Path;
                    try
                    {
                        if ((node.Attributes & FileAttributes.ReparsePoint) != 0) await output.Writer.WriteAsync(node, ct);
                        else
                            foreach (var item in DirectoryEntries(path, issues, ct, excluded))
                            {
                                ct.ThrowIfCancellationRequested();
                                if (excluded(item.Path)) continue;
                                if (item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) await output.Writer.WriteAsync(item, ct);
                                else if (item.IsDirectory)
                                {
                                    Interlocked.Increment(ref pending);
                                    directories.Writer.TryWrite(item);
                                }
                                else
                                {
                                    await output.Writer.WriteAsync(item, ct);
                                }
                            }
                    }
                    catch (Exception ex) when (Expected(ex)) { issues.Enqueue(new() { Path = path, Message = ex.Message }); }
                    finally { if (Interlocked.Decrement(ref pending) == 0) directories.Writer.TryComplete(); }
                }
            }
            catch { linked.Cancel(); throw; }
        }, ct)).ToArray();
        _ = Task.WhenAll(workers).ContinueWith(task => output.Writer.TryComplete(task.Exception), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            while (output.Reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
                while (output.Reader.TryRead(out var entry)) consume(entry);
            Task.WhenAll(workers).GetAwaiter().GetResult();
        }
        finally
        {
            linked.Cancel();
            try { Task.WhenAll(workers).GetAwaiter().GetResult(); } catch when (ct.IsCancellationRequested) { }
        }
    }

    private IEnumerable<InventoryEntry> DirectoryEntries(string path, ConcurrentQueue<ScanIssue> issues, CancellationToken token, Func<string, bool> excluded)
    {
        if (BatchDirectoryMetadata)
        {
            using var native = WindowsVolume.DirectoryEntries(path, token, true, verifyPath: !path.StartsWith(@"\\"),
                requiredSerial: RequiredVolumeSerial).GetEnumerator();
            while (true)
            {
                InventoryEntry entry;
                try
                {
                    if (!native.MoveNext()) yield break;
                    entry = native.Current;
                }
                catch (NotSupportedException ex) when (RequiredVolumeSerial.HasValue)
                { throw new IOException("Verified directory metadata is unavailable.", ex); }
                catch (NotSupportedException) { break; }
                yield return entry;
            }
        }
        if (RequiredVolumeSerial.HasValue)
            throw new IOException("This scan requires verified directory metadata.");
        // Filesystems/providers without the batch API retain the per-entry path.
        var current = WindowsVolume.ReadEntry(path, verifyPath: !path.StartsWith(@"\\"));
        yield return current;
        if (!current.IsDirectory || (current.Attributes & FileAttributes.ReparsePoint) != 0) yield break;
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            token.ThrowIfCancellationRequested();
            if (excluded(item.FullName)) continue;
            InventoryEntry? entry = null;
            try { entry = WindowsVolume.ReadEntry(item.FullName, verifyPath: !path.StartsWith(@"\\")); }
            catch (Exception ex) when (Expected(ex)) { issues.Enqueue(new() { Path = item.FullName, Message = ex.Message }); }
            if (entry is not null) yield return entry;
        }
    }

    private void ReadPathsParallel(IEnumerable<string> paths, Action<InventoryEntry> consume, ConcurrentQueue<ScanIssue> issues, CancellationToken token, int readers)
    {
        foreach (var batch in paths.Chunk(2048))
        {
            var entries = new InventoryEntry?[batch.Length];
            Parallel.For(0, batch.Length, new ParallelOptions { MaxDegreeOfParallelism = readers, CancellationToken = token }, i =>
            {
                try { entries[i] = WindowsVolume.ReadEntry(batch[i], verifyPath: true, requiredSerial: RequiredVolumeSerial); }
                catch (Exception ex) when (Expected(ex)) { issues.Enqueue(new() { Path = batch[i], Message = ex.Message }); }
            });
            foreach (var entry in entries) if (entry is not null) consume(entry);
        }
    }

    internal static void ExpandHardLinks(InventoryEntry entry, string root, Action<InventoryEntry> consume, Action<ScanIssue> issue,
        CancellationToken token, uint? requiredSerial = null)
    {
        if (entry.IsDirectory || entry.HardLinks <= 1) return;
        try
        {
            foreach (var alias in WindowsVolume.HardLinkPaths(entry.Path))
            {
                token.ThrowIfCancellationRequested();
                if (alias.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) || !IsBelow(alias, root)) continue;
                try { consume(WindowsVolume.ReadEntry(alias, verifyPath: true, requiredSerial: requiredSerial)); }
                catch (Exception ex) when (Expected(ex)) { issue(new() { Path = alias, Message = "Hard-link alias could not be read: " + ex.Message }); }
            }
        }
        catch (Exception ex) when (Expected(ex)) { issue(new() { Path = entry.Path, Message = "Hard-link aliases are incomplete: " + ex.Message }); }
    }

    private List<string> ChangedPaths(InventoryStore store, long basis, JournalCursor saved, JournalCursor head, CancellationToken token)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingDeletes = Path.Combine(head.Volume, "$Extend", "$Deleted");
        var pendingDeletePrefix = pendingDeletes + Path.DirectorySeparatorChar;
        using var connection = store.Open();
        IReadOnlyList<VolumeChange> changes;
        try { changes = Journal.Changes(saved, head, token); }
        catch (Exception ex) when (RequireJournalForDriveRefresh && ex is (IOException or System.ComponentModel.Win32Exception))
        {
            // Journal-reader errors describe the stream or checkpoint without naming a file.
            throw new JournalRefreshException(ex.Message, ex, ex.Message);
        }
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            var oldParent = saved.VolumeIdentity + ":" + change.ParentId.ToString("X16");
            var parent = store.IdentityPaths(connection, basis, oldParent, true).FirstOrDefault();
            if (parent is not null)
            {
                var oldPath = Path.Combine(parent, change.Name);
                if (IsBelow(oldPath, store.RootPath) && !oldPath.Equals(store.RootPath, StringComparison.OrdinalIgnoreCase)) paths.Add(oldPath);
            }
            var current = Journal.ResolveId(head.Volume, change.FileId);
            // An unlinked file can still resolve by ID under NTFS's pending-deletion directory.
            // Its previous path and indexed aliases still need to be reconciled.
            if (current is not null && !current.StartsWith(pendingDeletePrefix, StringComparison.OrdinalIgnoreCase)
                && !current.Equals(pendingDeletes, StringComparison.OrdinalIgnoreCase)
                && IsBelow(current, store.RootPath) && !current.Equals(store.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(current);
                try
                {
                    var info = WindowsVolume.ReadEntry(current, verifyPath: true, requiredSerial: RequiredVolumeSerial);
                    if (!info.IsDirectory && info.HardLinks > 1)
                        foreach (var alias in WindowsVolume.HardLinkPaths(current)) if (IsBelow(alias, store.RootPath)) paths.Add(alias);
                }
                catch (FileNotFoundException) { }
            }
            foreach (var alias in store.IdentityPaths(connection, basis, saved.VolumeIdentity + ":" + change.FileId.ToString("X16"), false)) paths.Add(alias);
        }
        return paths.ToList();
    }

    private void VerifyScopeAncestors(string scope, string root)
    {
        if (scope.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
        for (var parent = Path.GetDirectoryName(scope); parent is not null && IsBelow(parent, root); parent = Path.GetDirectoryName(parent))
        {
            try
            {
                var entry = WindowsVolume.ReadEntry(parent, verifyPath: true, requiredSerial: RequiredVolumeSerial);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The requested folder is beneath a skipped link: " + parent);
            }
            catch (FileNotFoundException) { }
            if (parent.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static string PromoteScope(InventoryStore store, Microsoft.Data.Sqlite.SqliteConnection connection, long scan, string path)
    {
        if (path.Equals(store.RootPath, StringComparison.OrdinalIgnoreCase)) return path;
        for (var parent = Path.GetDirectoryName(path); parent is not null && IsBelow(parent, store.RootPath); parent = Path.GetDirectoryName(parent))
        {
            if (!store.HasDirectory(connection, scan, parent)) path = parent;
            if (parent.Equals(store.RootPath, StringComparison.OrdinalIgnoreCase)) break;
        }
        return path;
    }

    internal static List<string> MinimizeScopes(IEnumerable<string> scopes, CancellationToken token)
    {
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var scope in scopes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length))
        {
            token.ThrowIfCancellationRequested();
            var covered = false;
            for (var parent = Path.GetDirectoryName(scope); parent is not null; parent = Path.GetDirectoryName(parent))
                if (chosen.Contains(parent)) { covered = true; break; }
            if (!covered && chosen.Add(scope)) result.Add(scope);
        }
        return result;
    }

    internal static bool IsBelow(string path, string root) => InventoryStore.Normalize(path).Equals(InventoryStore.Normalize(root), StringComparison.OrdinalIgnoreCase)
        || InventoryStore.Normalize(path).StartsWith(InventoryStore.Normalize(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}

internal sealed class JournalRefreshException(string message, Exception? inner = null, string? logReason = null) : IOException(message, inner)
{
    internal string LogReason { get; } = logReason ?? "Could not verify a changed entry or its hard links.";
}
