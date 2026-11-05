using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcSpaceMap.Services;

internal static class WriterChecks
{
    internal static void Run(string suite, Action<bool, string> check)
    {
        var root = Path.Combine(suite, "virtual-writer-root");
        var store = new InventoryStore(root, Path.Combine(suite, "writer-check.sqlite"), true);
        using var connection = store.Open();
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = new InventoryEntry(Path.Combine(root, "A'é😀.dat"), root, "A'é😀.dat", false,
            (1L << 43) + 27, 4096, "SERIAL:0000000000000123", modified, FileAttributes.Hidden);
        var second = new InventoryEntry(Path.Combine(root, "unknown.dat"), root, "unknown.dat", false,
            10, null, null, modified, FileAttributes.Normal);
        var rootEntry = new InventoryEntry(root, "ignored-parent", "root", true, 0, 0, "SERIAL:0000000000000001", modified, FileAttributes.Directory);
        InventoryStore.Execute(connection, "BEGIN");
        try
        {
            string Write(bool direct)
            {
                var id = store.Begin(connection, null);
                using (var writer = new InventoryWriter(connection, id, direct))
                {
                    writer.Write(rootEntry, 0);
                    writer.Write(first, 1);
                    writer.Write(second, 1);
                    writer.Write(first with { Size = 123, Allocated = null, Identity = null }, 1);
                    writer.Write(second with { Path = Path.Combine(root, "known.dat"), Name = "known.dat", Allocated = 0, Identity = "SERIAL:0000000000000004" }, 1);
                }
                using var command = InventoryStore.Command(connection, """
                    SELECT path,parent,name,dir,depth,size,allocated,identity,modified,attributes,files,folders,unique_allocated
                    FROM entries WHERE scan=$id ORDER BY path
                    """, ("$id", id));
                using var reader = command.ExecuteReader();
                var rows = new List<object[]>();
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    rows.Add(values);
                }
                return JsonSerializer.Serialize(rows);
            }
            check(Write(true) == Write(false), "direct SQLite writer matches ADO for Unicode, large sizes, null replacement and row metadata");
        }
        finally { InventoryStore.Execute(connection, "ROLLBACK"); }
        InventoryStore.Execute(connection, """
            CREATE TRIGGER reject_fixture BEFORE INSERT ON entries WHEN NEW.name='reject'
            BEGIN SELECT RAISE(ABORT,'Fixture write failure'); END;
            BEGIN;
            """);
        var rejected = false;
        try
        {
            var id = store.Begin(connection, null);
            using var writer = new InventoryWriter(connection, id);
            writer.Write(rootEntry, 0);
            writer.Write(first with { Name = "reject" }, 1);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { rejected = true; }
        finally { InventoryStore.Execute(connection, "ROLLBACK"); }
        using var count = InventoryStore.Command(connection, "SELECT count(*) FROM entries");
        check(rejected && (long)count.ExecuteScalar()! == 0 && store.LatestId() is null,
            "direct writer propagates database failures and failed inventory work rolls back");
    }
}
