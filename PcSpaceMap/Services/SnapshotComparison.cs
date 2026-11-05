using Microsoft.Data.Sqlite;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

internal static class SnapshotComparison
{
    internal static IReadOnlyList<InventoryChange> Best(IEnumerable<InventoryChange> changes, int limit)
    {
        if (limit == 0) return [];
        if (limit < 0) return changes.OrderByDescending(x => Math.Abs(x.DeltaBytes)).ThenBy(x => x.Path, PathOrder.Instance).ToList();
        var queue = new PriorityQueue<InventoryChange, InventoryChange>(new WorstFirst());
        foreach (var change in changes)
        {
            if (queue.Count < limit) queue.Enqueue(change, change);
            else queue.EnqueueDequeue(change, change);
        }
        return queue.UnorderedItems.Select(x => x.Element).OrderByDescending(x => Math.Abs(x.DeltaBytes)).ThenBy(x => x.Path, PathOrder.Instance).ToList();
    }
    // Both scans are stored in path order. Merge them rather than probing the old
    // B-tree once per new file, then probing the new B-tree once per old file.
    internal static IReadOnlyList<InventoryChange> Compare(SqliteConnection connection, long current,
        long? previous, int limit, CancellationToken token = default)
    {
        if (!previous.HasValue || limit == 0) return [];
        using var currentCommand = InventoryStore.Command(connection,
            "SELECT path,size,modified,dir FROM entries WHERE scan=$id ORDER BY path", ("$id", current));
        using var previousCommand = InventoryStore.Command(connection,
            "SELECT path,size,modified,dir FROM entries WHERE scan=$id ORDER BY path", ("$id", previous));
        using var now = currentCommand.ExecuteReader();
        using var old = previousCommand.ExecuteReader();
        var hasNow = now.Read();
        var hasOld = old.Read();
        var nowPath = hasNow ? now.GetString(0) : "";
        var oldPath = hasOld ? old.GetString(0) : "";
        var best = new PriorityQueue<InventoryChange, InventoryChange>(new WorstFirst());
        var unlimited = limit < 0 ? new List<InventoryChange>() : null;
        void Add(InventoryChange change)
        {
            if (unlimited is not null) { unlimited.Add(change); return; }
            if (best.Count < limit) best.Enqueue(change, change);
            else best.EnqueueDequeue(change, change);
        }
        while (hasNow || hasOld)
        {
            token.ThrowIfCancellationRequested();
            var order = !hasNow ? 1 : !hasOld ? -1 : PathOrder.Instance.Compare(nowPath, oldPath);
            if (order < 0)
                Add(new(nowPath, "Added", 0, now.GetInt64(1)));
            else if (order > 0)
                Add(new(oldPath, "Deleted", old.GetInt64(1), 0));
            else if (now.GetInt64(1) != old.GetInt64(1) || now.GetInt64(2) != old.GetInt64(2) || now.GetInt64(3) != old.GetInt64(3))
                Add(new(nowPath, "Changed", old.GetInt64(1), now.GetInt64(1)));
            if (order <= 0)
            {
                hasNow = now.Read();
                if (hasNow) nowPath = now.GetString(0);
            }
            if (order >= 0)
            {
                hasOld = old.Read();
                if (hasOld) oldPath = old.GetString(0);
            }
        }
        return ((IEnumerable<InventoryChange>?)unlimited ?? best.UnorderedItems.Select(x => x.Element))
            .OrderByDescending(x => Math.Abs(x.DeltaBytes)).ThenBy(x => x.Path, PathOrder.Instance).ToList();
    }

    private sealed class WorstFirst : IComparer<InventoryChange>
    {
        public int Compare(InventoryChange? x, InventoryChange? y)
        {
            var delta = Math.Abs(x!.DeltaBytes).CompareTo(Math.Abs(y!.DeltaBytes));
            return delta != 0 ? delta : PathOrder.Instance.Compare(y.Path, x.Path);
        }
    }

    // SQLite's built-in NOCASE folds ASCII, not every Unicode character. Using
    // OrdinalIgnoreCase here would merge distinct SQLite keys such as Ä and ä.
    private sealed class PathOrder : IComparer<string>
    {
        public static readonly PathOrder Instance = new();
        public int Compare(string? x, string? y)
        {
            var length = Math.Min(x!.Length, y!.Length);
            for (var i = 0; i < length; i++)
            {
                var left = x[i];
                var right = y[i];
                if (left is >= 'A' and <= 'Z') left = (char)(left + 32);
                if (right is >= 'A' and <= 'Z') right = (char)(right + 32);
                if (left != right)
                {
                    // UTF-8 byte order is code-point order, unlike UTF-16 order
                    // for supplementary characters. Compare scalar values there.
                    var leftValue = char.IsHighSurrogate(left) && i + 1 < x.Length && char.IsLowSurrogate(x[i + 1])
                        ? char.ConvertToUtf32(left, x[i + 1]) : left;
                    var rightValue = char.IsHighSurrogate(right) && i + 1 < y.Length && char.IsLowSurrogate(y[i + 1])
                        ? char.ConvertToUtf32(right, y[i + 1]) : right;
                    return leftValue.CompareTo(rightValue);
                }
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
