using System.Diagnostics;
using System.Text.Json;
using PcSpaceMap.Models;
using PcSpaceMap.Services;

internal static class CompactBenchmark
{
    internal static async Task RunAsync(string reportPath)
    {
        var suite = Path.Combine(Path.GetTempPath(), "pc-space-map-compact-benchmark-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(suite, "files");
        Directory.CreateDirectory(root);
        try
        {
            Console.WriteLine("Creating the disposable 20,000-file compact-storage fixture.");
            for (var directory = 0; directory < 100; directory++)
            {
                var path = Path.Combine(root, directory.ToString("D3")); Directory.CreateDirectory(path);
                for (var file = 0; file < 200; file++) File.WriteAllBytes(Path.Combine(path, file.ToString("D3") + ".dat"), new byte[file % 16 + 1]);
            }
            var oldScanner = new DiskScanner { DatabasePath = Path.Combine(suite, "legacy.sqlite"), CompactStorage = false, PreferNtfs = false };
            var scanner = new DiskScanner { DatabasePath = Path.Combine(suite, "compact.sqlite"), PreferNtfs = false };
            async Task<(ScanResult Result, double Seconds)> Timed(Func<Task<ScanResult>> operation)
            { var watch = Stopwatch.StartNew(); var result = await operation(); return (result, watch.Elapsed.TotalSeconds); }
            long DatabaseBytes(InventoryStore inventory)
            {
                using var connection = inventory.Open();
                using var count = InventoryStore.Command(connection, "PRAGMA page_count");
                using var size = InventoryStore.Command(connection, "PRAGMA page_size");
                return (long)count.ExecuteScalar()! * (long)size.ExecuteScalar()!;
            }
            var old = await Timed(() => oldScanner.ScanAsync(root, null, default));
            var fresh = await Timed(() => scanner.ScanAsync(root, null, default));
            var legacyBytes = DatabaseBytes(old.Result.Inventory!);
            var compactBytes = DatabaseBytes(fresh.Result.Inventory!);
            var scope = Path.Combine(root, "050"); var changed = Path.Combine(scope, "100.dat");
            File.WriteAllBytes(changed, new byte[801]);
            var oldUpdate = await Timed(() => oldScanner.RefreshAsync(old.Result, scope, null, default));
            var updated = await Timed(() => scanner.RefreshAsync(fresh.Result, scope, null, default));
            File.WriteAllBytes(changed, new byte[1200]);
            var updatedAgain = await Timed(() => scanner.RefreshAsync(updated.Result, scope, null, default));
            var oldAgain = await Timed(() => oldScanner.RefreshAsync(oldUpdate.Result, scope, null, default));
            if (old.Result.Root.Size != fresh.Result.Root.Size || oldAgain.Result.Root.Size != updatedAgain.Result.Root.Size
                || oldAgain.Result.FileCount != updatedAgain.Result.FileCount
                || oldAgain.Result.Root.UniqueAllocatedSize != updatedAgain.Result.Root.UniqueAllocatedSize)
                throw new Exception("Compact and legacy totals differ.");
            var identical = await Timed(() => scanner.RefreshAsync(updatedAgain.Result, scope, null, default));
            var repeat = await Timed(() => scanner.ScanAsync(root, null, default));
            if (identical.Result.Work!.VersionsWritten != 0 || repeat.Result.Work!.VersionsWritten != 0)
                throw new Exception("Unchanged metadata was rewritten.");
            var tunedScanner = new DiskScanner { DatabasePath = scanner.DatabasePath, PreferNtfs = false, AutoTuneReaders = true,
                Tuner = new ReaderTuner(Path.Combine(suite, "reader-profiles.json")), Parallelism = 8 };
            var tuned = await Timed(() => tunedScanner.ScanAsync(root, null, default));
            var reused = await tunedScanner.RefreshAsync(tuned.Result, scope, null, default);
            if (!reused.Work!.ReaderProfileReused || reused.Work.ReaderCount != tuned.Result.Work!.ReaderCount)
                throw new Exception("Actual filesystem reader calibration was not reused.");
            var report = new { Files = fresh.Result.FileCount, Folders = fresh.Result.DirectoryCount, IdenticalTotals = true,
                LegacyInitialScanSeconds = old.Seconds, CompactInitialScanSeconds = fresh.Seconds,
                LegacyInitialDatabaseBytes = legacyBytes, CompactInitialDatabaseBytes = compactBytes,
                LegacyFolderUpdateSeconds = Math.Min(oldUpdate.Seconds, oldAgain.Seconds),
                CompactFolderUpdateSeconds = Math.Min(updated.Seconds, updatedAgain.Seconds),
                FolderUpdateWork = updatedAgain.Result.Work, UnchangedFolderUpdateSeconds = identical.Seconds,
                UnchangedFolderWork = identical.Result.Work, RepeatedFullScanSeconds = repeat.Seconds, RepeatedFullScanWork = repeat.Result.Work,
                CalibratedReaders = tuned.Result.Work!.ReaderCount, ReaderProbeSeconds = tuned.Result.Work.ReaderProbeSeconds,
                ReaderProfileReused = reused.Work.ReaderProfileReused,
                LegacyBytesAfterHeldSnapshots = DatabaseBytes(old.Result.Inventory!), CompactBytesAfterHeldSnapshots = DatabaseBytes(fresh.Result.Inventory!),
                Method = "Disposable fixture on the same volume; both engines use batched metadata and direct writes; folder update order is reversed in the second round; database sizes are allocated page bytes; all reports remain local" };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
        }
        finally
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(Path.GetFullPath(suite)) != parent || !Path.GetFileName(suite).StartsWith("pc-space-map-compact-benchmark-"))
                throw new InvalidOperationException("Unexpected compact fixture cleanup path.");
            Directory.Delete(suite, true);
        }
    }
}
