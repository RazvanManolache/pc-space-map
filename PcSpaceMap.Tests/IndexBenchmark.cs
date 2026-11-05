using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcSpaceMap.Services;
using PcSpaceMap.Models;

internal static class IndexBenchmark
{
    public static async Task CompareAsync(string root, string reportPath)
    {
        root = InventoryStore.Normalize(root);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        var database = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCSpaceMap", "inventories", key + ".sqlite");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        InventoryStore.Execute(connection, "PRAGMA cache_size=-65536; BEGIN");
        using var snapshot = InventoryStore.Command(connection, "SELECT max(id) FROM scans WHERE completed IS NOT NULL");
        var id = (long)snapshot.ExecuteScalar()!;
        using var prior = InventoryStore.Command(connection, "SELECT max(id) FROM scans WHERE id<$id AND completed IS NOT NULL", ("$id", id));
        var previous = prior.ExecuteScalar() as long?;
        using var saved = InventoryStore.Command(connection, "SELECT changes FROM scans WHERE id=$id", ("$id", id));
        var expected = JsonSerializer.Deserialize<List<InventoryChange>>((string)saved.ExecuteScalar()!) ?? [];
        Console.WriteLine($"Comparing cached snapshots {previous} and {id}; main database is read-only.");
        var clock = Stopwatch.StartNew();
        var actual = SnapshotComparison.Compare(connection, id, previous, 5000);
        var seconds = clock.Elapsed.TotalSeconds;
        var matches = actual.SequenceEqual(expected);
        var report = new { Root = root, Snapshot = id, Previous = previous, DatabaseReadOnly = true,
            ComparisonSeconds = seconds, Changes = actual.Count, MatchesSavedSqlComparison = matches,
            PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
        InventoryStore.Execute(connection, "ROLLBACK");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        if (!matches) throw new Exception("Sequential comparison differs from the saved SQL comparison.");
    }

    public static async Task RunAsync(string root, string reportPath)
    {
        root = InventoryStore.Normalize(root);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        var database = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCSpaceMap", "inventories", key + ".sqlite");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        InventoryStore.Execute(connection, "PRAGMA cache_size=-65536; PRAGMA temp.cache_size=-32768");
        using var snapshot = InventoryStore.Command(connection, "SELECT max(id) FROM scans WHERE completed IS NOT NULL");
        var id = (long)snapshot.ExecuteScalar()!;
        var clock = Stopwatch.StartNew();
        Console.WriteLine("Benchmarking cached snapshot " + id + " (main database is read-only).");
        var keepers = FolderRollups.DuplicateKeepers(connection, id, default);
        var duplicatesSeconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"Duplicate groups: {keepers.Count:N0} in {duplicatesSeconds:F2}s");
        clock.Restart();
        var totals = FolderRollups.Calculate(connection, id, default, keepers) ?? throw new Exception("Directory memory budget exceeded.");
        var rollupSeconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"Computed {totals.Folders.Count:N0} folder totals in {rollupSeconds:F2}s");
        clock.Restart();
        long differences = 0;
        var index = 0;
        using (var command = InventoryStore.Command(connection, "SELECT path,size,allocated,unique_allocated,files,folders FROM entries WHERE scan=$id AND dir=1 ORDER BY path", ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var computed = totals.Folders[index++];
                if (computed.Path != reader.GetString(0) || computed.Size != reader.GetInt64(1)
                    || computed.AllocatedBytes != (reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2))
                    || computed.UniqueBytes != (reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3))
                    || computed.Files != reader.GetInt64(4) || computed.Folders != reader.GetInt64(5)) differences++;
            }
        }
        if (index != totals.Folders.Count) differences++;
        var rootTotal = totals.Folders.Single(x => x.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
        var report = new { Root = root, Snapshot = id, DatabaseReadOnly = true, DuplicateGroups = keepers.Count,
            DuplicatesSeconds = duplicatesSeconds, RollupSeconds = rollupSeconds, ValidationSeconds = clock.Elapsed.TotalSeconds,
            DirectoryCount = totals.Folders.Count, rootTotal.Size, rootTotal.Files, rootTotal.Folders,
            FolderMismatches = differences, totals.UniquePolicyMismatches, totals.OrphanFiles,
            PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        if (differences != 0 || totals.UniquePolicyMismatches != 0) throw new Exception("Optimized totals differ from stored inventory.");
    }
}
