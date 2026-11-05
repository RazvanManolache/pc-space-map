using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcSpaceMap.Services;

internal static class QueryBenchmark
{
    internal static async Task RunAsync(string root, string folder, string reportPath)
    {
        root = InventoryStore.Normalize(root);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        var database = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCSpaceMap", "inventories", key + ".sqlite");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        InventoryStore.Execute(connection, "PRAGMA cache_size=-65536; BEGIN");
        using var snapshot = InventoryStore.Command(connection, "SELECT max(id) FROM scans WHERE completed IS NOT NULL");
        var scan = (long)snapshot.ExecuteScalar()!;
        var range = InventoryStore.SubtreeRange(folder);
        Console.WriteLine("Timing folder selection against cached snapshot " + scan + "; main database is read-only.");
        using var legacy = InventoryStore.Command(connection,
            "SELECT count(*) FROM entries WHERE scan=$id AND (path=$root OR substr(path,1,length($prefix))=$prefix COLLATE NOCASE)",
            ("$id", scan), ("$root", range.Path), ("$prefix", range.Prefix));
        var clock = Stopwatch.StartNew();
        var oldCount = (long)legacy.ExecuteScalar()!;
        var oldMs = clock.Elapsed.TotalMilliseconds;
        using var indexed = InventoryStore.Command(connection, """
            SELECT (SELECT count(*) FROM entries WHERE scan=$id AND path=$root)
                 + (SELECT count(*) FROM entries WHERE scan=$id AND path>$prefix AND path<$end)
            """, ("$id", scan), ("$root", range.Path), ("$prefix", range.Prefix), ("$end", range.End));
        clock.Restart();
        var newCount = (long)indexed.ExecuteScalar()!;
        var newMs = clock.Elapsed.TotalMilliseconds;
        InventoryStore.Execute(connection, "ROLLBACK");
        if (oldCount != newCount) throw new Exception("Indexed folder selection differs from the legacy predicate.");
        Console.WriteLine($"Folder rows {newCount}: {oldMs:F1}ms -> {newMs:F3}ms.");
        var cleanup = BenchmarkCleanup();
        var report = new { Root = root, Folder = folder, Snapshot = scan, DatabaseReadOnly = true,
            MatchedRows = newCount, LegacyFilterMilliseconds = oldMs, IndexedFilterMilliseconds = newMs, Cleanup = cleanup };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
    }

    private static object BenchmarkCleanup()
    {
        var now = DateTime.UtcNow;
        var modified = now.AddDays(-240);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fixture = Path.GetPathRoot(home)! + @"PerformanceFixture\project\a-long-directory-name\another-long-directory-name\";
        var files = Enumerable.Range(0, 200_000).Select(i => (Path: (i % 8) switch
        {
            0 => fixture + @"src\" + i + ".dll",
            1 => Path.Combine(Path.GetTempPath(), "benchmark-" + i + ".TMP"),
            2 => fixture + @"Google\Chrome\User Data\Default\Cache\Cache_Data\" + i,
            3 => fixture + @".NuGet\Packages\package\1.0\" + i + ".dll",
            4 => Path.Combine(home, "Downloads", "benchmark-" + i + ".EXE"),
            5 => fixture + @"logs\" + i + ".ETL",
            6 => fixture + @"$Recycle.Bin\user\" + i,
            _ => fixture + @"artifacts\" + i + ".rar"
        }, Size: i % 8 == 5 ? 200L * 1024 * 1024 : 100L)).ToArray();
        // Warm both implementations before timed replay. No filesystem files are created.
        var warmOld = new LegacyCleanupAnalyzer(now);
        var warmNew = new CleanupAnalyzer(now);
        foreach (var file in files.Take(2000))
        {
            warmOld.Consider(file.Path, file.Size, modified);
            warmNew.Consider(file.Path, file.Size, modified);
        }
        (double Seconds, long Bytes, string Signature) Measure(bool original)
        {
            var old = new LegacyCleanupAnalyzer(now);
            var updated = new CleanupAnalyzer(now);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            foreach (var file in files)
                if (original) old.Consider(file.Path, file.Size, modified);
                else updated.Consider(file.Path, file.Size, modified);
            var seconds = watch.Elapsed.TotalSeconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var suggestions = original ? old.Build() : updated.Build();
            return (seconds, bytes, JsonSerializer.Serialize(suggestions.OrderBy(x => x.Category)));
        }
        var oldRun = Measure(true);
        var newRun = Measure(false);
        var newAgain = Measure(false);
        var oldAgain = Measure(true);
        var matched = oldRun.Signature == newRun.Signature && oldAgain.Signature == newAgain.Signature;
        if (!matched) throw new Exception("Cleanup replay differs from the legacy classifier.");
        return new { ReplayedFiles = files.Length, LegacySeconds = Math.Min(oldRun.Seconds, oldAgain.Seconds),
            OptimizedSeconds = Math.Min(newRun.Seconds, newAgain.Seconds), LegacyAllocatedBytes = oldRun.Bytes,
            OptimizedAllocatedBytes = newRun.Bytes, IdenticalSuggestions = matched,
            Method = "Two alternating replay rounds; best elapsed time per implementation; no filesystem scan" };
    }
}
