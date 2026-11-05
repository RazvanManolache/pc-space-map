using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace PcSpaceMap.Services;

// Bind by position on a reusable SQLite statement. This avoids per-file ADO.NET
// parameter-name lookups, numeric boxing and data-reader construction.
internal interface IInventoryWriter : IDisposable
{
    void Write(InventoryEntry entry, int depth, bool skipped = false);
}

internal sealed class InventoryWriter : IInventoryWriter
{
    private const string Sql = """
        INSERT OR REPLACE INTO entries(scan,path,parent,name,dir,depth,size,allocated,identity,modified,attributes,files,folders,unique_allocated)
        VALUES($scan,$path,$parent,$name,$dir,$depth,$size,$allocated,$identity,$modified,$attributes,CASE WHEN $dir=0 THEN 1 ELSE 0 END,0,$allocated)
        """;
    private readonly SqliteConnection _connection;
    private readonly long _scan;
    private readonly sqlite3_stmt? _statement;
    private readonly SqliteCommand? _legacy;

    internal InventoryWriter(SqliteConnection connection, long scan, bool direct = true)
    {
        _connection = connection;
        _scan = scan;
        if (!direct)
        {
            _legacy = InventoryStore.Command(connection, Sql, ("$scan", scan), ("$path", ""), ("$parent", ""),
                ("$name", ""), ("$dir", 0), ("$depth", 0), ("$size", 0L), ("$allocated", null),
                ("$identity", null), ("$modified", 0L), ("$attributes", 0L));
            return;
        }
        var code = raw.sqlite3_prepare_v2(connection.Handle, Sql, out var statement);
        if (code != raw.SQLITE_OK)
        {
            statement?.Dispose();
            Check(code);
        }
        _statement = statement ?? throw new InvalidOperationException("Inventory statement was not prepared.");
        try { Check(raw.sqlite3_bind_int64(_statement, 1, scan)); }
        catch { _statement.Dispose(); throw; }
    }

    public void Write(InventoryEntry entry, int depth, bool skipped = false)
    {
        if (skipped)
        {
            InventoryStore.Execute(_connection, "INSERT OR IGNORE INTO skipped_entries VALUES($id,$path)", ("$id", _scan), ("$path", entry.Path));
            return;
        }
        var parent = depth == 0 ? "" : entry.Parent;
        if (_legacy is { } command)
        {
            command.Parameters[1].Value = entry.Path;
            command.Parameters[2].Value = parent;
            command.Parameters[3].Value = entry.Name;
            command.Parameters[4].Value = entry.IsDirectory ? 1 : 0;
            command.Parameters[5].Value = depth;
            command.Parameters[6].Value = entry.Size;
            command.Parameters[7].Value = entry.Allocated is { } allocation ? allocation : DBNull.Value;
            command.Parameters[8].Value = (object?)entry.Identity ?? DBNull.Value;
            command.Parameters[9].Value = entry.Modified.Ticks;
            command.Parameters[10].Value = (long)entry.Attributes;
            command.ExecuteNonQuery();
            return;
        }
        var statement = _statement!;
        Check(raw.sqlite3_reset(statement));
        Check(raw.sqlite3_bind_text(statement, 2, entry.Path));
        Check(raw.sqlite3_bind_text(statement, 3, parent));
        Check(raw.sqlite3_bind_text(statement, 4, entry.Name));
        Check(raw.sqlite3_bind_int(statement, 5, entry.IsDirectory ? 1 : 0));
        Check(raw.sqlite3_bind_int(statement, 6, depth));
        Check(raw.sqlite3_bind_int64(statement, 7, entry.Size));
        Check(entry.Allocated is { } allocated ? raw.sqlite3_bind_int64(statement, 8, allocated) : raw.sqlite3_bind_null(statement, 8));
        Check(entry.Identity is { } identity ? raw.sqlite3_bind_text(statement, 9, identity) : raw.sqlite3_bind_null(statement, 9));
        Check(raw.sqlite3_bind_int64(statement, 10, entry.Modified.Ticks));
        Check(raw.sqlite3_bind_int64(statement, 11, (long)entry.Attributes));
        var code = raw.sqlite3_step(statement);
        if (code != raw.SQLITE_DONE) Check(code);
    }

    private void Check(int code)
    {
        if (code == raw.SQLITE_OK) return;
        throw new SqliteException(raw.sqlite3_errmsg(_connection.Handle).utf8_to_string() ?? "Inventory write failed.",
            code & 0xff, raw.sqlite3_extended_errcode(_connection.Handle));
    }

    public void Dispose()
    {
        _legacy?.Dispose();
        _statement?.Dispose();
    }
}
