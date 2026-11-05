using System.Diagnostics;
using System.Text.Json;
using PcSpaceMap.Services;

internal static class ScanBenchmark
{
    internal static async Task RunAsync(string reportPath)
    {
        var suite = Path.Combine(Path.GetTempPath(), "pc-space-map-benchmark-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(suite, "files");
        Directory.CreateDirectory(root);
        try
        {
            Console.WriteLine("Creating a disposable 20,000-file scan fixture.");
            for (var folder = 0; folder < 100; folder++)
            {
                var directory = Path.Combine(root, folder.ToString("D3"));
                Directory.CreateDirectory(directory);
                for (var file = 0; file < 200; file++)
                    File.WriteAllBytes(Path.Combine(directory, file.ToString("D3") + ".dat"), new byte[(file % 16) + 1]);
            }
            var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Prepend(root).ToArray();
            double Metadata(bool batch)
            {
                long size = 0, count = 0;
                var watch = Stopwatch.StartNew();
                foreach (var directory in directories)
                    if (batch)
                        foreach (var entry in WindowsVolume.DirectoryEntries(directory, default))
                        { if (!entry.IsDirectory) { size += entry.Size; count++; } }
                    else
                        foreach (var path in Directory.EnumerateFiles(directory))
                        { var entry = WindowsVolume.ReadEntry(path); size += entry.Size; count++; }
                if (count != 20_000 || size != 166_800) throw new Exception("Metadata enumeration totals differ.");
                return watch.Elapsed.TotalSeconds;
            }
            // Reverse the order on the second round to reduce filesystem-cache order bias.
            var metadataLegacy = Metadata(false);
            var metadataBatch = Metadata(true);
            var metadataBatchAgain = Metadata(true);
            var metadataLegacyAgain = Metadata(false);
            async Task<(PcSpaceMap.Models.ScanResult Result, double Seconds)> Scan(bool batch, string suffix)
            {
                var watch = Stopwatch.StartNew();
                var result = await new DiskScanner { PreferNtfs = false, BatchDirectoryMetadata = batch, DirectInventoryWrites = batch,
                    CompactStorage = batch,
                    DatabasePath = Path.Combine(suite, suffix + ".sqlite") }.ScanAsync(root, null, default);
                return (result, watch.Elapsed.TotalSeconds);
            }
            var legacy = await Scan(false, "legacy");
            var updated = await Scan(true, "batched");
            var updatedAgain = await Scan(true, "batched-again");
            var legacyAgain = await Scan(false, "legacy-again");
            var matches = new[] { legacy.Result, updated.Result, updatedAgain.Result, legacyAgain.Result }.All(result =>
                result.Root.Size == 166_800 && result.FileCount == 20_000 && result.DirectoryCount == 101
                && result.Root.AllocatedSize == legacy.Result.Root.AllocatedSize
                && result.Root.UniqueAllocatedSize == legacy.Result.Root.UniqueAllocatedSize && result.Issues.Count == 0);
            if (!matches) throw new Exception("End-to-end inventory results differ.");
            var report = new { Files = 20_000, Directories = 101, Size = 166_800, IdenticalTotals = matches,
                LegacyMetadataSeconds = Math.Min(metadataLegacy, metadataLegacyAgain),
                BatchedMetadataSeconds = Math.Min(metadataBatch, metadataBatchAgain),
                LegacyFullScanSeconds = Math.Min(legacy.Seconds, legacyAgain.Seconds),
                BatchedFullScanSeconds = Math.Min(updated.Seconds, updatedAgain.Seconds),
                LegacyTimings = legacy.Result.Timings, BatchedTimings = updated.Result.Timings,
                Method = "Two alternating rounds; best elapsed time; disposable fixture; per-file metadata/ADO/legacy storage versus batched metadata/compact storage; full scans include persistence, summaries, review and reopening" };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
        }
        finally
        {
            // The target is a unique fixture created above, never a user-data directory.
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(Path.GetFullPath(suite)) != expectedParent || !Path.GetFileName(suite).StartsWith("pc-space-map-benchmark-"))
                throw new InvalidOperationException("Unexpected fixture cleanup path.");
            Directory.Delete(suite, true);
        }
    }
}
