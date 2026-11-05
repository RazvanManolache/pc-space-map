using Microsoft.Data.Sqlite;

namespace PcSpaceMap.Services;

internal sealed class CompactCatalog : IDisposable
{
    private readonly Dictionary<string, (long Id, string Path)> _directories = new(StringComparer.Ordinal);
    private readonly NativeStatement _directory, _node, _insertNode, _insertDirectory;
    internal CompactCatalog(SqliteConnection connection)
    {
        _directory = new(connection, "SELECT node,path FROM c_directories WHERE path=?1");
        _node = new(connection, "SELECT id,name FROM c_nodes WHERE parent=?1 AND name=?2");
        _insertNode = new(connection, "INSERT INTO c_nodes(parent,name) VALUES(?1,?2) RETURNING id");
        _insertDirectory = new(connection, "INSERT OR IGNORE INTO c_directories VALUES(?1,?2,?3)");
    }
    internal long Node(long parent, string name, out bool created, out string storedName)
    {
        _node.Reset(); _node.Number(1, parent); _node.Text(2, name);
        created = !_node.Step();
        storedName = created ? name : _node.Text(1)!;
        if (!created) return _node.Number(0);
        _insertNode.Reset(); _insertNode.Number(1, parent); _insertNode.Text(2, name);
        if (!_insertNode.Step()) throw new InvalidOperationException("Node was not inserted.");
        var id = _insertNode.Number(0);
        _insertNode.Step();
        return id;
    }
    internal long Directory(string path) => Directory(path, out _);
    internal long Directory(string path, out string storedPath)
    {
        storedPath = path;
        if (path.Length == 0) return 0;
        if (_directories.TryGetValue(path, out var known)) { storedPath = known.Path; return known.Id; }
        _directory.Reset(); _directory.Text(1, path);
        long id;
        if (_directory.Step()) { id = _directory.Number(0); storedPath = _directory.Text(1)!; }
        else
        {
            var parent = Path.GetDirectoryName(path) ?? "";
            var parentId = Directory(parent, out var storedParent);
            var name = parent.Length == 0 ? path : Path.GetFileName(path);
            id = Node(parentId, name, out _, out var storedName);
            storedPath = parentId == 0 ? storedName : Path.Combine(storedParent, storedName);
            _insertDirectory.Reset(); _insertDirectory.Number(1, id); _insertDirectory.Text(2, storedPath);
            _insertDirectory.Number(3, path.Count(c => c == '\\')); _insertDirectory.Step();
        }
        if (_directories.Count >= 16_384) _directories.Clear();
        _directories[path] = (id, storedPath);
        return id;
    }
    public void Dispose() { _directory.Dispose(); _node.Dispose(); _insertNode.Dispose(); _insertDirectory.Dispose(); }
}

internal sealed class CompactWriter : IInventoryWriter
{
    private readonly CompactInventory _inventory;
    private readonly SqliteConnection _connection;
    private readonly long _scan;
    private readonly CompactCatalog _catalog;
    private readonly CleanupAnalyzer _cleanup = new();
    private readonly NativeStatement _current, _close, _deleteOwn, _insert, _seen, _dirty, _affected, _identity, _prior;
    private readonly HashSet<long> _parents = [];
    private readonly Dictionary<long, (string Name, string? Path)> _spelling = [];
    internal CompactWriter(CompactInventory inventory, SqliteConnection connection, long scan)
    {
        _inventory = inventory; _connection = connection; _scan = scan; _catalog = new(connection);
        _current = new(connection, "SELECT first,dir,size,allocated,identity,modified,attributes,files,folders,unique_allocated,skipped FROM c_versions WHERE node=?1 AND until IS NULL");
        _close = new(connection, "UPDATE c_versions SET until=?1 WHERE node=?2 AND until IS NULL AND first<>?1");
        _deleteOwn = new(connection, "DELETE FROM c_versions WHERE node=?1 AND first=?2");
        _insert = new(connection, "INSERT INTO c_versions VALUES(?1,?2,NULL,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15)");
        _seen = new(connection, "INSERT OR IGNORE INTO seen_nodes VALUES(?1)");
        _dirty = new(connection, "INSERT OR IGNORE INTO changed_nodes VALUES(?1)");
        _affected = new(connection, "INSERT OR IGNORE INTO affected_folders VALUES(?1)");
        _identity = new(connection, "INSERT OR IGNORE INTO changed_identities VALUES(?1)");
        _prior = new(connection, "INSERT OR IGNORE INTO prior_identities VALUES(?1)");
        using var command = InventoryStore.Command(connection, "SELECT node,name,path FROM c_spelling WHERE until IS NULL");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            _spelling.Add(reader.GetInt64(0), (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
    }
    private void Identity(string? identity, bool prior = false)
    {
        if (identity is null) return;
        if (prior) { _prior.Reset(); _prior.Text(1, identity); _prior.Step(); }
        if (!_inventory.IsFullScan) { _identity.Reset(); _identity.Text(1, identity); _identity.Step(); }
    }
    private void Affected(long node)
    {
        if (_parents.Count >= 65_536) _parents.Clear();
        if (node == 0 || !_parents.Add(node)) return;
        _affected.Reset(); _affected.Number(1, node); _affected.Step();
    }
    public void Write(InventoryEntry entry, int depth, bool skipped = false)
    {
        var parent = _catalog.Directory(entry.Parent);
        var created = false;
        var node = entry.IsDirectory
            ? _catalog.Directory(entry.Path, out var storedPath)
            : _catalog.Node(parent, entry.Name, out created, out storedPath);
        RecordSpelling(node, entry, storedPath);
        if (_inventory.HasPrevious) { _seen.Reset(); _seen.Number(1, node); _seen.Step(); }
        _current.Reset(); _current.Number(1, node);
        var exists = !created && _current.Step();
        var oldDir = exists && _current.Number(1) != 0;
        var size = entry.IsDirectory && oldDir ? _current.Number(2) : entry.Size;
        var allocated = entry.IsDirectory && oldDir ? _current.Optional(3) : entry.Allocated;
        var files = entry.IsDirectory ? oldDir ? _current.Number(7) : 0 : 1;
        var folders = entry.IsDirectory && oldDir ? _current.Number(8) : 0;
        var unique = entry.IsDirectory && oldDir ? _current.Optional(9) : entry.Allocated;
        var unchanged = exists && oldDir == entry.IsDirectory && _current.Number(10) == (skipped ? 1 : 0)
            && _current.Text(4) == entry.Identity && _current.Number(5) == entry.Modified.Ticks
            && _current.Number(6) == (long)entry.Attributes
            && (entry.IsDirectory || (_current.Number(2) == entry.Size && _current.Optional(3) == entry.Allocated));
        if (unchanged) { _current.Reset(); return; }
        if (exists && !oldDir && _current.Number(10) == 0) Identity(_current.Text(4), true);
        _current.Reset();
        if (!entry.IsDirectory && !skipped) Identity(entry.Identity);
        if (exists)
        {
            _close.Reset(); _close.Number(1, _scan); _close.Number(2, node); _close.Step();
            _deleteOwn.Reset(); _deleteOwn.Number(1, node); _deleteOwn.Number(2, _scan); _deleteOwn.Step();
        }
        _insert.Reset();
        _insert.Number(1, node); _insert.Number(2, _scan); _insert.Number(3, parent); _insert.Number(4, entry.IsDirectory ? 1 : 0); _insert.Number(5, depth);
        _insert.Number(6, size); _insert.Optional(7, allocated); _insert.Text(8, entry.Identity);
        _insert.Number(9, entry.Modified.Ticks); _insert.Number(10, (long)entry.Attributes);
        _insert.Number(11, skipped ? 0 : files); _insert.Number(12, skipped ? 0 : folders); _insert.Optional(13, unique);
        _insert.Number(14, skipped ? 1 : 0); _insert.Number(15, skipped || entry.IsDirectory ? 0 : _cleanup.MatchMask(entry.Path, entry.Size));
        _insert.Step();
        if (_inventory.HasPrevious) { _dirty.Reset(); _dirty.Number(1, node); _dirty.Step(); }
        Affected(parent);
        if (entry.IsDirectory) Affected(node);
    }
    private void RecordSpelling(long node, InventoryEntry entry, string stored)
    {
        var differs = entry.IsDirectory ? entry.Path != stored : entry.Name != stored;
        var value = (entry.Name, entry.IsDirectory ? entry.Path : null);
        var hadOverride = _spelling.TryGetValue(node, out var prior);
        if (hadOverride)
        {
            if (differs && value == prior) return;
            InventoryStore.Execute(_connection, """
                DELETE FROM c_spelling WHERE node=$node AND first=$scan;
                UPDATE c_spelling SET until=$scan WHERE node=$node AND until IS NULL;
                """, ("$node", node), ("$scan", _scan));
            _spelling.Remove(node);
        }
        if (differs)
        {
            InventoryStore.Execute(_connection, "INSERT INTO c_spelling VALUES($node,$scan,NULL,$name,$path)",
                ("$node", node), ("$scan", _scan), ("$name", value.Name), ("$path", value.Item2));
            _spelling[node] = value;
        }
        if ((hadOverride || differs) && _inventory.HasPrevious)
        {
            _dirty.Reset(); _dirty.Number(1, node); _dirty.Step();
        }
    }
    public void Dispose()
    {
        _catalog.Dispose(); _current.Dispose(); _close.Dispose(); _deleteOwn.Dispose(); _insert.Dispose();
        _seen.Dispose(); _dirty.Dispose(); _affected.Dispose(); _identity.Dispose(); _prior.Dispose();
    }
}
