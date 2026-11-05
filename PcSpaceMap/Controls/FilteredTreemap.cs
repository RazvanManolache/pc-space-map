using System.IO;
using PcSpaceMap.Models;

namespace PcSpaceMap.Controls;

internal sealed class FilteredTreemap
{
    private readonly Dictionary<string, ScanNode> _folders = new(StringComparer.OrdinalIgnoreCase);

    public ScanNode Root { get; }

    public FilteredTreemap(string rootPath, IEnumerable<ScanNode> files, CancellationToken token)
    {
        Root = AddFolder(Path.TrimEndingDirectorySeparator(rootPath), null);
        var missing = new Stack<string>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            ScanNode? parent = null;
            for (var path = Path.GetDirectoryName(file.Path); path is not null; path = Path.GetDirectoryName(path))
            {
                if (_folders.TryGetValue(path, out parent)) break;
                missing.Push(path);
            }
            if (parent is null)
                throw new InvalidOperationException("A matching file is outside the selected folder.");

            while (missing.TryPop(out var path)) parent = AddFolder(path, parent);
            parent.Children.Add(file);
            for (ScanNode? folder = parent; folder is not null; folder = folder.Parent)
            {
                folder.Size += file.Size;
                folder.FileCount++;
            }
        }
    }

    public ScanNode? FindFolder(string path) => _folders.GetValueOrDefault(Path.TrimEndingDirectorySeparator(path));

    private ScanNode AddFolder(string path, ScanNode? parent)
    {
        var name = Path.GetFileName(path);
        var folder = new ScanNode
        {
            Path = path, Name = name.Length == 0 ? path : name, IsDirectory = true, Parent = parent
        };
        _folders.Add(path, folder);
        parent?.Children.Add(folder);
        return folder;
    }
}
