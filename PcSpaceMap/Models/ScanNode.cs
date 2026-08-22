using System.IO;

namespace PcSpaceMap.Models;

public sealed class ScanNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public long Size { get; set; }
    public long FileCount { get; set; }
    public long DirectoryCount { get; set; }
    public DateTime LastWriteUtc { get; init; }
    public FileAttributes Attributes { get; init; }
    public ScanNode? Parent { get; set; }
    public List<ScanNode> Children { get; } = [];

    public string SizeText => SizeFormatter.Format(Size);
    public string TypeText => IsDirectory ? "Folder" : (System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "File");
    public string ModifiedText => LastWriteUtc == DateTime.MinValue ? "—" : LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string ContentsText => IsDirectory ? $"{FileCount:N0} files · {DirectoryCount:N0} folders" : TypeText;

    public IEnumerable<ScanNode> DescendantsAndSelf()
    {
        var stack = new Stack<ScanNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (var i = current.Children.Count - 1; i >= 0; i--)
                stack.Push(current.Children[i]);
        }
    }
}

public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0) return "—";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:N1} {Units[unit]}";
    }
}
