using Microsoft.Data.Sqlite;
using PcSpaceMap.Services;
using PcSpaceMap.Models;

internal static class SnapshotComparisonChecks
{
    internal static void Run(Action<bool, string> check)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        InventoryStore.Execute(connection, """
            CREATE TABLE entries(scan INTEGER,path TEXT COLLATE NOCASE,size INTEGER,modified INTEGER,dir INTEGER,
                PRIMARY KEY(scan,path)) WITHOUT ROWID;
            BEGIN;
            """);
        using var insert = InventoryStore.Command(connection, "INSERT INTO entries VALUES($s,$p,$b,$m,$d)",
            ("$s", 0), ("$p", ""), ("$b", 0L), ("$m", 0L), ("$d", 0));
        void Add(int scan, string path, long size, long modified = 0, int dir = 0)
        {
            insert.Parameters[0].Value = scan;
            insert.Parameters[1].Value = path;
            insert.Parameters[2].Value = size;
            insert.Parameters[3].Value = modified;
            insert.Parameters[4].Value = dir;
            insert.ExecuteNonQuery();
        }
        for (var i = 0; i < 6000; i++)
        {
            var path = $@"X:\fixture\{i:D5}.dat";
            Add(1, path, 100);
            if (i % 5 != 0) Add(2, i % 5 == 1 ? path + "-new" : path,
                i % 5 == 2 ? 200 : 100, i % 5 == 3 ? 1 : 0, i % 5 == 4 ? 1 : 0);
        }
        foreach (var suffix in new[] { "Ä", "ä", "", "U00010000", "U00010400", "U0001F600", "same" })
        {
            Add(1, @"X:\fixture\" + suffix, 99);
            Add(2, @"X:\fixture\" + suffix, 100);
        }
        Add(1, @"X:\fixture\CASE", 0);
        Add(2, @"X:\fixture\case", 0, 1);
        Add(1, @"X:\fixture\unchanged", 0);
        Add(2, @"X:\fixture\unchanged", 0);
        InventoryStore.Execute(connection, "COMMIT");
        check(new[] { 0, 1, 17, 5000, -1 }.All(limit =>
            SnapshotComparison.Compare(connection, 2, 1, limit)
                .SequenceEqual(SqlReference(connection, 2, 1, limit))),
            "sequential snapshot comparison matches SQL for additions, deletion, metadata, limits, ties and Unicode ordering");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            SnapshotComparison.Compare(connection, 2, 1, 5000, cancelled.Token);
            check(false, "snapshot comparison cancellation");
        }
        catch (OperationCanceledException) { check(true, "snapshot comparison supports cancellation"); }
    }

    private static IReadOnlyList<InventoryChange> SqlReference(SqliteConnection connection, long current, long previous, int limit)
    {
        using var command = InventoryStore.Command(connection, """
            SELECT path,kind,before,after FROM (
              SELECT c.path AS path,CASE WHEN p.path IS NULL THEN 'Added' ELSE 'Changed' END AS kind,
                coalesce(p.size,0) AS before,c.size AS after FROM entries c LEFT JOIN entries p ON p.scan=$old AND p.path=c.path
                WHERE c.scan=$new AND (p.path IS NULL OR c.size<>p.size OR c.modified<>p.modified OR c.dir<>p.dir)
              UNION ALL SELECT p.path,'Deleted',p.size,0 FROM entries p LEFT JOIN entries c ON c.scan=$new AND c.path=p.path
                WHERE p.scan=$old AND c.path IS NULL
            ) ORDER BY abs(after-before) DESC,path LIMIT $limit
            """, ("$old", previous), ("$new", current), ("$limit", limit));
        using var reader = command.ExecuteReader();
        var result = new List<InventoryChange>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        return result;
    }
}
