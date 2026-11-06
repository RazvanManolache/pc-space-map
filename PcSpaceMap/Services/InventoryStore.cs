using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

// The UI holds folder summaries; the complete inventory remains on disk.
public sealed class InventoryStore
{
    private static readonly ConcurrentDictionary<string, ConcurrentBag<WeakReference<ScanResult>>> OpenResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly CompactInventory? _compact;
    private bool _readOnly;
    private HashSet<string>? _legacyColumns;
    internal bool IsCompact => _compact is not null;
    internal void SetReaderSelection(ReaderSelection selection) => _compact?.SetReaderSelection(selection);
    public string DatabasePath { get; }
    public string RootPath { get; }
    internal TimeSpan HistoryGrace { get; set; }
    public InventoryStore(string root, string? databasePath = null) : this(root, databasePath, false) { }

    internal InventoryStore(string root, string? databasePath, bool legacy, bool readOnly = false)
    {
        RootPath = Normalize(root);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RootPath.ToUpperInvariant())))[..24];
        DatabasePath = databasePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCSpaceMap", "inventories", key + (legacy ? ".sqlite" : ".v3.sqlite"));
        _readOnly = readOnly;
        if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open();
        if (!legacy)
        {
            using var existing = Command(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='entries'");
            legacy = (long)existing.ExecuteScalar()! != 0;
            if (legacy) _readOnly = true;
        }
        if (!legacy)
        {
            _compact = new CompactInventory(this);
            if (!readOnly) _compact.Initialize(connection);
            return;
        }
        if (_readOnly)
        {
            using var oldSchema = Command(connection, "PRAGMA table_info(scans)");
            using var reader = oldSchema.ExecuteReader();
            _legacyColumns = [];
            while (reader.Read()) _legacyColumns.Add(reader.GetString(1));
            return;
        }
        Execute(connection, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS scans(id INTEGER PRIMARY KEY, root TEXT NOT NULL, completed TEXT,
                method TEXT NOT NULL DEFAULT '', journal TEXT, issues TEXT NOT NULL DEFAULT '[]', skipped INTEGER NOT NULL DEFAULT 0,
                suggestions TEXT, changes TEXT);
            CREATE TABLE IF NOT EXISTS entries(scan INTEGER NOT NULL, path TEXT COLLATE NOCASE NOT NULL,
                parent TEXT COLLATE NOCASE NOT NULL, name TEXT NOT NULL, dir INTEGER NOT NULL, depth INTEGER NOT NULL,
                size INTEGER NOT NULL, allocated INTEGER, identity TEXT, modified INTEGER NOT NULL, attributes INTEGER NOT NULL,
                files INTEGER NOT NULL DEFAULT 0, folders INTEGER NOT NULL DEFAULT 0, unique_allocated INTEGER,
                PRIMARY KEY(scan,path)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS entry_parent ON entries(scan,parent,size DESC);
            CREATE INDEX IF NOT EXISTS entry_size ON entries(scan,dir,size DESC);
            CREATE INDEX IF NOT EXISTS entry_depth ON entries(scan,depth);
            CREATE INDEX IF NOT EXISTS entry_identity ON entries(scan,identity);
            CREATE TABLE IF NOT EXISTS skipped_entries(scan INTEGER NOT NULL,path TEXT COLLATE NOCASE NOT NULL,PRIMARY KEY(scan,path)) WITHOUT ROWID;
            """);
        using var schema = Command(connection, "PRAGMA table_info(scans)");
        var columns = new HashSet<string>();
        using (var reader = schema.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        foreach (var column in new[] { "suggestions", "changes", "timings" })
            if (!columns.Contains(column)) Execute(connection, $"ALTER TABLE scans ADD COLUMN {column} TEXT");
    }

    public static ScanResult? OpenServiceSnapshot(string root, string databasePath, long? snapshot = null, string? folder = null)
    {
        if (!File.Exists(databasePath)) return null;
        var store = new InventoryStore(root, databasePath, false, readOnly: true);
        return (snapshot ?? store.LatestId()) is { } id ? store.Load(id, folder: folder) : null;
    }

    public static long? ServiceSnapshotId(string root, string databasePath) =>
        File.Exists(databasePath) ? new InventoryStore(root, databasePath, false, readOnly: true).LatestId() : null;

    public bool FolderUnchanged(string folder, long before, long after) =>
        before > 0 && before < after && DiskScanner.IsBelow(folder, RootPath)
        && _compact?.FolderUnchanged(folder, before, after) == true;

    internal IDisposable KeepWalOpen()
    {
        var connection = Open();
        try
        {
            const int persistWal = 10;
            int enabled = 1;
            if (sqlite3_file_control(connection.Handle!, "main", persistWal, ref enabled) != 0)
                throw new IOException("Could not retain the read-only service index's SQLite sidecars.");
            Execute(connection, "SELECT count(*) FROM scans");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_file_control(SQLitePCL.sqlite3 database, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int operation, ref int value);

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            // Preserve a literal NTFS leaf such as nul or con.txt while still resolving .. in its parent.
            const string marker = "__pc_space_map_literal__";
            var anchor = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path) ?? ".", marker));
            full = anchor[..^marker.Length] + Path.GetFileName(path);
        }
        return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false,
            Mode = _readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA cache_size=-65536; PRAGMA temp.cache_size=-32768;");
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        using var command = Command(connection, sql, args);
        command.ExecuteNonQuery();
    }

    internal static SqliteCommand Command(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    public long? LatestId()
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT max(id) FROM scans WHERE completed IS NOT NULL");
        return command.ExecuteScalar() is long id ? id : null;
    }

    internal long Begin(SqliteConnection connection, long? copyFrom)
    {
        if (_compact is not null) return _compact.Begin(connection, copyFrom);
        Execute(connection, "INSERT INTO scans(root) VALUES($root)", ("$root", RootPath));
        using var cmd = Command(connection, "SELECT last_insert_rowid()");
        var id = (long)cmd.ExecuteScalar()!;
        if (copyFrom.HasValue)
        {
            Execute(connection, """
                INSERT INTO entries SELECT $new,path,parent,name,dir,depth,size,allocated,identity,modified,attributes,files,folders,unique_allocated
                FROM entries WHERE scan=$old
                """, ("$new", id), ("$old", copyFrom));
            Execute(connection, "INSERT INTO skipped_entries SELECT $new,path FROM skipped_entries WHERE scan=$old", ("$new", id), ("$old", copyFrom));
        }
        return id;
    }

    internal IInventoryWriter CreateWriter(SqliteConnection connection, long scan, bool direct) =>
        _compact is null ? new InventoryWriter(connection, scan, direct) : _compact.CreateWriter(connection, scan);
    internal void RemoveSnapshotTree(SqliteConnection connection, long scan, string path)
    {
        if (_compact is null) RemoveTree(connection, scan, path); else _compact.IncludeScope(connection, path);
    }
    internal void FinishEnumeration(SqliteConnection connection, long scan, CancellationToken token) => _compact?.FinishEnumeration(connection, scan, token);
    internal IReadOnlyList<CleanupSuggestion>? AggregateSnapshot(SqliteConnection connection, long scan, CancellationToken token, long budget) =>
        _compact is null ? Aggregate(connection, scan, token, budget) : _compact.Aggregate(connection, scan, token);
    internal void CompleteSnapshot(SqliteConnection connection, long scan, string method, JournalCursor? cursor, IReadOnlyList<ScanIssue> issues,
        CancellationToken token, IReadOnlyList<CleanupSuggestion>? suggestions)
    {
        if (_compact is null)
        {
            using var root = Command(connection, "SELECT count(*) FROM entries WHERE scan=$id AND path=$root AND dir=1", ("$id", scan), ("$root", RootPath));
            if ((long)root.ExecuteScalar()! != 1) throw new DirectoryNotFoundException("The inventory root could not be read. The previous snapshot was preserved.");
            using var count = Command(connection, "SELECT count(*) FROM skipped_entries WHERE scan=$id", ("$id", scan));
            Complete(connection, scan, method, cursor, issues, (long)count.ExecuteScalar()!, token, suggestions);
        }
        else _compact.Complete(connection, scan, method, cursor, issues, token, suggestions!);
    }
    internal IReadOnlyList<string> IdentityPaths(SqliteConnection connection, long scan, string identity, bool directory)
    {
        if (_compact is not null) return _compact.IdentityPaths(connection, scan, identity, directory);
        using var command = Command(connection, "SELECT path FROM entries INDEXED BY entry_identity WHERE scan=$id AND identity=$identity AND dir=$dir",
            ("$id", scan), ("$identity", identity), ("$dir", directory ? 1 : 0));
        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    internal static void RemoveTree(SqliteConnection connection, long scan, string path)
    {
        var range = SubtreeRange(path);
        Execute(connection, """
            DELETE FROM entries WHERE scan=$id AND path=$p;
            DELETE FROM entries WHERE scan=$id AND path>$prefix AND path<$end;
            DELETE FROM skipped_entries WHERE scan=$id AND path=$p;
            DELETE FROM skipped_entries WHERE scan=$id AND path>$prefix AND path<$end;
            """, ("$id", scan), ("$p", range.Path), ("$prefix", range.Prefix), ("$end", range.End));
    }

    internal static (string Path, string Prefix, string End) SubtreeRange(string path)
    {
        var normalized = Normalize(path);
        var stem = normalized.TrimEnd('\\');
        // All descendants start with stem + '\\'. ']' immediately follows '\\'
        // in SQLite's NOCASE order, including for Unicode and wildcard filenames.
        return (normalized, stem + "\\", stem + "]");
    }

    internal static IReadOnlyList<CleanupSuggestion>? Aggregate(SqliteConnection connection, long scan, CancellationToken token, long memoryBudget = 768L * 1024 * 1024)
    {
        token.ThrowIfCancellationRequested();
        FolderRollups.DuplicateKeepers(connection, scan, token);
        Execute(connection, """
            UPDATE entries SET unique_allocated=allocated WHERE scan=$id AND dir=0 AND unique_allocated IS NOT allocated;
            UPDATE entries INDEXED BY entry_identity SET unique_allocated=0 WHERE scan=$id AND dir=0
                AND identity IN (SELECT identity FROM duplicate_files)
                AND path<>(SELECT keeper FROM duplicate_files WHERE identity=entries.identity);
            """, ("$id", scan));
        var totals = FolderRollups.Calculate(connection, scan, token, memoryBudget: memoryBudget, collectCleanup: true);
        if (totals is not null)
        {
            FolderRollups.Save(connection, scan, totals, token);
            return totals.Suggestions;
        }
        AggregateLowMemory(connection, scan, token);
        return null;
    }

    private static void AggregateLowMemory(SqliteConnection connection, long scan, CancellationToken token)
    {
        Execute(connection, """
            UPDATE entries SET files=CASE WHEN dir=0 THEN 1 ELSE 0 END, folders=0,
                size=CASE WHEN dir=1 THEN 0 ELSE size END,
                allocated=CASE WHEN dir=1 THEN 0 ELSE allocated END WHERE scan=$id;
            UPDATE entries SET unique_allocated=0 WHERE scan=$id AND dir=1;
            """, ("$id", scan));
        using var max = Command(connection, "SELECT coalesce(max(depth),0) FROM entries WHERE scan=$id", ("$id", scan));
        var depth = Convert.ToInt32(max.ExecuteScalar());
        for (; depth >= 0; depth--)
        {
            token.ThrowIfCancellationRequested();
            Execute(connection, """
                CREATE TEMP TABLE IF NOT EXISTS rollup(parent TEXT PRIMARY KEY COLLATE NOCASE,size INTEGER,allocated INTEGER,unique_allocated INTEGER,files INTEGER,folders INTEGER);
                DELETE FROM rollup;
                INSERT INTO rollup SELECT parent,sum(size),CASE WHEN count(allocated)=count(*) THEN sum(allocated) END,
                    CASE WHEN count(unique_allocated)=count(*) THEN sum(unique_allocated) END,sum(files),sum(folders+dir)
                    FROM entries WHERE scan=$id AND depth=$depth+1 GROUP BY parent;
                UPDATE entries SET (size,allocated,unique_allocated,files,folders)=
                    (SELECT size,allocated,unique_allocated,files,folders FROM rollup WHERE parent=entries.path)
                    WHERE scan=$id AND dir=1 AND depth=$depth AND path IN (SELECT parent FROM rollup);
                """, ("$id", scan), ("$depth", depth));
        }
    }

    internal static void Complete(SqliteConnection connection, long scan, string method, JournalCursor? journal,
        IReadOnlyList<ScanIssue> issues, long skipped, CancellationToken token = default, IReadOnlyList<CleanupSuggestion>? suggestions = null)
    {
        if (suggestions is null)
        {
            var cleanup = new CleanupAnalyzer();
            using (var files = Command(connection, "SELECT path,size,modified FROM entries INDEXED BY sqlite_autoindex_entries_1 WHERE scan=$id AND dir=0", ("$id", scan)))
            using (var reader = files.ExecuteReader())
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    cleanup.Consider(reader.GetString(0), reader.GetInt64(1), new DateTime(reader.GetInt64(2), DateTimeKind.Utc));
                }
            suggestions = cleanup.Build();
        }
        using var prior = Command(connection, "SELECT max(id) FROM scans WHERE id<$id AND completed IS NOT NULL", ("$id", scan));
        var previous = prior.ExecuteScalar() as long?;
        var changes = CompareCore(connection, scan, previous, 5000, token);
        Execute(connection, "UPDATE scans SET completed=$time,method=$method,journal=$journal,issues=$issues,skipped=$skipped,suggestions=$suggestions,changes=$changes WHERE id=$id",
            ("$time", DateTime.UtcNow.ToString("O")), ("$method", method), ("$journal", journal is null ? null : JsonSerializer.Serialize(journal)),
            ("$issues", JsonSerializer.Serialize(issues)), ("$skipped", skipped), ("$id", scan),
            ("$suggestions", JsonSerializer.Serialize(suggestions)), ("$changes", JsonSerializer.Serialize(changes)));
    }

    internal JournalCursor? ReadJournal(long scan)
    {
        if (_legacyColumns is not null && !_legacyColumns.Contains("journal")) return null;
        using var connection = Open();
        using var cmd = Command(connection, "SELECT journal FROM scans WHERE id=$id", ("$id", scan));
        return cmd.ExecuteScalar() is string text ? JsonSerializer.Deserialize<JournalCursor>(text) : null;
    }

    private const string Columns = "path,name,dir,size,modified,attributes,files,folders,allocated,unique_allocated";
    internal ScanNode Node(SqliteDataReader row, long scan, ScanNode? parent = null)
    {
        var path = row.GetString(0);
        return new()
        {
        Path = row.GetString(0), Name = row.GetString(1), IsDirectory = row.GetInt64(2) != 0,
        Size = row.GetInt64(3), LastWriteUtc = new DateTime(row.GetInt64(4), DateTimeKind.Utc),
        Attributes = (FileAttributes)row.GetInt64(5), FileCount = row.GetInt64(6), DirectoryCount = row.GetInt64(7),
        AllocatedSize = row.IsDBNull(8) ? null : row.GetInt64(8), UniqueAllocatedSize = row.IsDBNull(9) ? null : row.GetInt64(9),
        Parent = parent, ChildrenLoader = node => Children(scan, node),
        ChildCountLoader = () => ChildCount(scan, path),
        InventoryEnumerator = () => Enumerate(scan, path)
        };
    }

    public long ChildCount(long scan, string rowPath)
    {
        if (_compact is not null) return _compact.ChildCount(scan, rowPath);
        using var connection = Open();
        using var cmd = Command(connection, "SELECT count(*) FROM entries WHERE scan=$id AND parent=$path", ("$id", scan), ("$path", rowPath));
        return (long)cmd.ExecuteScalar()!;
    }

    public List<ScanNode> Children(long scan, ScanNode parent, int offset = 0, int limit = 350, FileFilter? nameFilter = null,
        CancellationToken token = default)
    {
        if (_compact is not null) return _compact.Children(scan, parent, offset, limit, nameFilter, token);
        using var connection = Open();
        var condition = NameFilterCondition(connection, nameFilter, "name");
        using var cmd = Command(connection, $"SELECT {Columns} FROM entries WHERE scan=$id AND parent=$parent{condition} ORDER BY size DESC,path LIMIT $limit OFFSET $offset",
            ("$id", scan), ("$parent", parent.Path), ("$limit", limit), ("$offset", offset));
        ObserveQueryCancellation(connection, token);
        return ReadNodes(cmd, reader => Node(reader, scan, parent), token);
    }

    internal bool HasDirectory(SqliteConnection connection, long scan, string path)
    {
        if (_compact is not null) return CompactInventory.HasDirectory(connection, scan, path);
        using var command = Command(connection, "SELECT 1 FROM entries WHERE scan=$scan AND path=$path AND dir=1",
            ("$scan", scan), ("$path", path));
        return command.ExecuteScalar() is not null;
    }

    public ScanNode? Find(long scan, string path)
    {
        if (_compact is not null) return _compact.Find(scan, path);
        using var connection = Open();
        using var cmd = Command(connection, $"SELECT {Columns} FROM entries WHERE scan=$id AND path=$path", ("$id", scan), ("$path", Normalize(path)));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var node = Node(reader, scan);
        reader.Close();
        if (!node.Path.Equals(RootPath, StringComparison.OrdinalIgnoreCase))
            node.Parent = Find(scan, Path.GetDirectoryName(node.Path)!);
        return node;
    }

    public IReadOnlyList<ScanNode> Largest(long scan, string? under = null, int limit = 5000, FileFilter? filter = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_compact is not null) return _compact.Largest(scan, under, limit, filter, token);
        using var connection = Open();
        ObserveQueryCancellation(connection, token);
        var range = SubtreeRange(under ?? RootPath);
        var global = range.Path.Equals(RootPath, StringComparison.OrdinalIgnoreCase);
        // For small subtrees the size index can walk the entire drive before finding
        // enough matches. Force the path range, then sort only that subtree.
        var condition = FileFilterCondition(connection, filter, "size", "modified", "name");
        var sql = global
            ? $"SELECT {Columns} FROM entries WHERE scan=$id AND dir=0{condition} ORDER BY size DESC LIMIT $limit"
            : $"SELECT {Columns} FROM entries INDEXED BY sqlite_autoindex_entries_1 WHERE scan=$id AND dir=0 AND path>$prefix AND path<$end{condition} ORDER BY size DESC,path LIMIT $limit";
        using var cmd = Command(connection, sql, ("$id", scan), ("$prefix", range.Prefix), ("$end", range.End), ("$limit", limit),
            ("$minBytes", filter?.MinimumBytes ?? 0), ("$before", filter?.ModifiedBefore?.Ticks ?? 0),
            ("$since", filter?.ModifiedSince?.Ticks ?? 0));
        return ReadNodes(cmd, reader => Node(reader, scan), token);
    }

    public IReadOnlyList<ScanNode> SearchNames(long scan, string under, FileFilter nameFilter, int offset, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (nameFilter is { Name.Length: 0, ModifiedBefore: null, ModifiedSince: null } || limit <= 0 || offset < 0) return [];
        if (_compact is not null) return _compact.SearchNames(scan, under, nameFilter, offset, limit, token);
        using var connection = Open();
        var range = SubtreeRange(under);
        var condition = NameFilterCondition(connection, nameFilter, "name") + ModificationFilterCondition(nameFilter, "modified");
        using var command = Command(connection,
            $"SELECT {Columns} FROM entries WHERE scan=$id AND path>$prefix AND path<$end{condition} ORDER BY path LIMIT $limit OFFSET $offset",
            ("$id", scan), ("$prefix", range.Prefix), ("$end", range.End), ("$limit", limit), ("$offset", offset),
            ("$before", nameFilter.ModifiedBefore?.Ticks ?? 0), ("$since", nameFilter.ModifiedSince?.Ticks ?? 0));
        ObserveQueryCancellation(connection, token);
        return ReadNodes(command, reader => Node(reader, scan), token);
    }

    public IReadOnlyList<CleanupSuggestion> FilterCleanup(long scan, string under, DateTime completed, FileFilter nameFilter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_compact is not null) return _compact.FilterCleanup(scan, under, completed, nameFilter, token);
        var cleanup = new CleanupAnalyzer();
        foreach (var file in Enumerate(scan, under))
        {
            token.ThrowIfCancellationRequested();
            if (!file.IsDirectory && nameFilter.MatchesName(file.Name)) cleanup.Consider(file.Path, file.Size, file.LastWriteUtc);
        }
        return cleanup.Build();
    }

    internal static void ObserveQueryCancellation(SqliteConnection connection, CancellationToken token)
    {
        if (token.CanBeCanceled)
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1024, _ => token.IsCancellationRequested ? 1 : 0, null);
    }

    internal static List<ScanNode> ReadNodes(SqliteCommand command, Func<SqliteDataReader, ScanNode> readNode, CancellationToken token)
    {
        try
        {
            using var reader = command.ExecuteReader();
            var result = new List<ScanNode>();
            while (reader.Read()) result.Add(readNode(reader));
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9 && token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }

    internal static string FileFilterCondition(SqliteConnection connection, FileFilter? filter, string size, string modified, string name)
    {
        if (filter is null) return "";
        return $" AND {size}>=$minBytes" + ModificationFilterCondition(filter, modified)
            + NameFilterCondition(connection, filter, name);
    }

    internal static string ModificationFilterCondition(FileFilter filter, string modified) =>
        (filter.ModifiedBefore.HasValue ? $" AND {modified}>0 AND {modified}<=$before" : "")
        + (filter.ModifiedSince.HasValue ? $" AND {modified}>0 AND {modified}>=$since" : "");

    internal static string NameFilterCondition(SqliteConnection connection, FileFilter? filter, string name)
    {
        if (filter is null || filter.Name.Length == 0) return "";
        connection.CreateFunction<string, bool>("inventory_name_matches", filter.MatchesName, isDeterministic: true);
        return $" AND inventory_name_matches({name})";
    }

    public IEnumerable<ScanNode> Enumerate(long scan, string? under = null)
    {
        if (_compact is not null)
        {
            foreach (var node in _compact.Enumerate(scan, under)) yield return node;
            yield break;
        }
        using var connection = Open();
        var range = SubtreeRange(under ?? RootPath);
        using var cmd = Command(connection, $"""
            SELECT {Columns} FROM entries WHERE scan=$id AND path=$root
            UNION ALL SELECT {Columns} FROM entries WHERE scan=$id AND path>$prefix AND path<$end
            ORDER BY path
            """, ("$id", scan), ("$root", range.Path), ("$prefix", range.Prefix), ("$end", range.End));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return Node(reader, scan);
    }

    public IReadOnlyList<InventoryChange> Compare(long current, long? previous, int limit = 5000)
    {
        if (_compact is not null) return _compact.Compare(current, previous, limit);
        using var connection = Open();
        return CompareCore(connection, current, previous, limit);
    }

    private static IReadOnlyList<InventoryChange> CompareCore(SqliteConnection connection, long current, long? previous, int limit, CancellationToken token = default) =>
        SnapshotComparison.Compare(connection, current, previous, limit, token);

    public ScanResult Load(long scan, TimeSpan duration = default, IReadOnlyList<ScanPhaseTiming>? timings = null, string? folder = null)
    {
        using var connection = Open();
        if (IsCompact) Execute(connection, "BEGIN");
        string OptionalColumn(string name) => _legacyColumns is not null && !_legacyColumns.Contains(name) ? "NULL" : name;
        using var cmd = Command(connection, $"SELECT completed,method,issues,skipped,{OptionalColumn("suggestions")},{OptionalColumn("changes")},{OptionalColumn("timings")} FROM scans WHERE id=$id AND completed IS NOT NULL", ("$id", scan));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("This inventory is incomplete.");
        var completed = DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind);
        var method = reader.GetString(1);
        var issues = JsonSerializer.Deserialize<List<ScanIssue>>(reader.GetString(2)) ?? [];
        var skipped = reader.GetInt64(3);
        IReadOnlyList<CleanupSuggestion>? suggestions = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<List<CleanupSuggestion>>(reader.GetString(4));
        var changes = reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<List<InventoryChange>>(reader.GetString(5));
        timings ??= reader.IsDBNull(6) ? [] : JsonSerializer.Deserialize<List<ScanPhaseTiming>>(reader.GetString(6)) ?? [];
        reader.Close();
        ScanWork? work = null;
        if (IsCompact)
        {
            using var workCommand = Command(connection, "SELECT work FROM scans WHERE id=$id", ("$id", scan));
            if (workCommand.ExecuteScalar() is string json) work = JsonSerializer.Deserialize<ScanWork>(json);
        }
        using var priorCmd = Command(connection, "SELECT max(id) FROM scans WHERE id<$id AND completed IS NOT NULL", ("$id", scan));
        var previous = priorCmd.ExecuteScalar() as long?;
        var scoped = folder is not null && !string.Equals(folder, RootPath, StringComparison.OrdinalIgnoreCase);
        var nodes = !scoped ? _compact?.LoadNodes(connection, scan) : null;
        var root = nodes?.Root ?? Find(scan, scoped ? folder! : RootPath);
        if (root?.IsDirectory != true || !DiskScanner.IsBelow(root.Path, RootPath))
            throw new DirectoryNotFoundException("The requested folder is unavailable in the saved index.");
        root.Parent = null;
        if (scoped && _compact is not null)
            (suggestions, skipped) = _compact.LoadFolderReview(connection, scan, root.Path, completed);
        else if (suggestions is null || scoped)
        {
            var cleanup = new CleanupAnalyzer();
            foreach (var file in Enumerate(scan, scoped ? root.Path : null))
                if (!file.IsDirectory) cleanup.Consider(file.Path, file.Size, file.LastWriteUtc);
            suggestions = cleanup.Build();
        }
        changes ??= Compare(scan, previous).ToList();
        if (scoped)
        {
            issues = issues.Where(issue => DiskScanner.IsBelow(issue.Path, root.Path)).ToList();
            changes = changes.Where(change => DiskScanner.IsBelow(change.Path, root.Path)).ToList();
        }
        var result = new ScanResult { Root = root, Inventory = this, SnapshotId = scan, ScanMethod = method, CompletedUtc = completed,
            LargestFiles = nodes?.Largest ?? Largest(scan, scoped ? root.Path : null), Suggestions = suggestions, Issues = issues,
            Duration = duration == default ? TimeSpan.FromSeconds(timings.Sum(x => x.Seconds)) : duration,
            FileCount = root.FileCount, DirectoryCount = root.DirectoryCount + 1, SkippedReparsePoints = skipped,
            Changes = changes, Timings = timings, Work = work };
        OpenResults.GetOrAdd(DatabasePath, _ => []).Add(new(result));
        return result;
    }

    internal void SaveTimings(long scan, IReadOnlyList<ScanPhaseTiming> timings)
    {
        using var connection = Open();
        Execute(connection, "UPDATE scans SET timings=$timings WHERE id=$id", ("$timings", JsonSerializer.Serialize(timings)), ("$id", scan));
    }

    internal void PruneHistory()
    {
        var live = OpenResults.GetOrAdd(DatabasePath, _ => []);
        var keep = live.Select(reference => reference.TryGetTarget(out var result) ? result.SnapshotId : 0).Where(id => id > 0).ToHashSet();
        using var connection = Open();
        using (var latest = Command(connection, "SELECT id FROM scans WHERE completed IS NOT NULL ORDER BY id DESC LIMIT 2"))
        using (var reader = latest.ExecuteReader()) while (reader.Read()) keep.Add(reader.GetInt64(0));
        if (HistoryGrace > TimeSpan.Zero)
        {
            using var recent = Command(connection, "SELECT id FROM scans WHERE completed >= $cutoff",
                ("$cutoff", (DateTime.UtcNow - HistoryGrace).ToString("O")));
            using var reader = recent.ExecuteReader();
            while (reader.Read()) keep.Add(reader.GetInt64(0));
        }
        using var all = Command(connection, "SELECT id FROM scans");
        var remove = new List<long>();
        using (var reader = all.ExecuteReader()) while (reader.Read()) if (!keep.Contains(reader.GetInt64(0))) remove.Add(reader.GetInt64(0));
        if (_compact is not null)
        {
            _compact.Prune(connection, keep, remove);
            return;
        }
        foreach (var id in remove)
        {
            Execute(connection, "DELETE FROM entries WHERE scan=$id; DELETE FROM skipped_entries WHERE scan=$id; DELETE FROM scans WHERE id=$id", ("$id", id));
        }
        // Deleted pages are reused by later scans. Avoid an expensive VACUUM after every inventory.
    }
}

internal sealed record InventoryEntry(string Path, string Parent, string Name, bool IsDirectory, long Size,
    long? Allocated, string? Identity, DateTime Modified, FileAttributes Attributes, uint HardLinks = 1);
