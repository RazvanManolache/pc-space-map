using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace PcSpaceMap.Services;

internal sealed class NativeStatement : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly sqlite3_stmt _statement;
    internal NativeStatement(SqliteConnection connection, string sql)
    {
        _connection = connection;
        var code = raw.sqlite3_prepare_v2(connection.Handle, sql, out var statement);
        if (code != raw.SQLITE_OK) { statement?.Dispose(); Check(code); }
        _statement = statement ?? throw new InvalidOperationException("Statement was not prepared.");
    }
    internal void Reset() => Check(raw.sqlite3_reset(_statement));
    internal void Number(int index, long value) => Check(raw.sqlite3_bind_int64(_statement, index, value));
    internal void Optional(int index, long? value) => Check(value.HasValue ? raw.sqlite3_bind_int64(_statement, index, value.Value) : raw.sqlite3_bind_null(_statement, index));
    internal void Text(int index, string? value) => Check(value is null ? raw.sqlite3_bind_null(_statement, index) : raw.sqlite3_bind_text(_statement, index, value));
    internal bool Step()
    {
        var code = raw.sqlite3_step(_statement);
        if (code == raw.SQLITE_ROW) return true;
        if (code == raw.SQLITE_DONE) return false;
        Check(code);
        return false;
    }
    internal long Number(int column) => raw.sqlite3_column_int64(_statement, column);
    internal long? Optional(int column) => raw.sqlite3_column_type(_statement, column) == raw.SQLITE_NULL ? null : Number(column);
    internal string? Text(int column) => raw.sqlite3_column_type(_statement, column) == raw.SQLITE_NULL ? null : raw.sqlite3_column_text(_statement, column).utf8_to_string();
    private void Check(int code)
    {
        if (code != raw.SQLITE_OK)
            throw new SqliteException(raw.sqlite3_errmsg(_connection.Handle).utf8_to_string() ?? "Inventory operation failed.", code & 255, raw.sqlite3_extended_errcode(_connection.Handle));
    }
    public void Dispose() => _statement.Dispose();
}
