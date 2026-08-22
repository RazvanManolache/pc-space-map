using System.Diagnostics;
using System.IO;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

public sealed class DiskScanner
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        MatchCasing = MatchCasing.PlatformDefault
    };

    public Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(rootPath, progress, cancellationToken), cancellationToken);

    private static ScanResult Scan(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var normalizedRoot = System.IO.Path.GetFullPath(rootPath);
        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException($"The folder does not exist: {normalizedRoot}");

        var rootInfo = new DirectoryInfo(normalizedRoot);
        var root = CreateDirectoryNode(rootInfo, null);
        var issues = new List<ScanIssue>();
        var cleanup = new CleanupAnalyzer();
        var largestFiles = new List<ScanNode>();
        long files = 0, directories = 1, skippedReparse = 0, bytesFound = 0, visited = 0;

        var stack = new Stack<DirectoryFrame>();
        stack.Push(new DirectoryFrame(rootInfo, root));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = stack.Peek();

            if (!frame.Started)
            {
                frame.Started = true;
                try
                {
                    frame.Enumerator = frame.Directory.EnumerateFileSystemInfos("*", EnumerationOptions).GetEnumerator();
                }
                catch (Exception ex) when (IsExpectedFileSystemError(ex))
                {
                    issues.Add(new ScanIssue { Path = frame.Directory.FullName, Message = ex.Message });
                    stack.Pop();
                    FinishDirectory(frame.Node);
                    continue;
                }
            }

            FileSystemInfo? entry = null;
            bool hasNext;
            try
            {
                hasNext = frame.Enumerator!.MoveNext();
                if (hasNext) entry = frame.Enumerator.Current;
            }
            catch (Exception ex) when (IsExpectedFileSystemError(ex))
            {
                issues.Add(new ScanIssue { Path = frame.Directory.FullName, Message = ex.Message });
                hasNext = false;
            }

            if (!hasNext)
            {
                frame.Enumerator?.Dispose();
                stack.Pop();
                FinishDirectory(frame.Node);
                if (frame.Node.Parent is not null)
                {
                    frame.Node.Parent.Size += frame.Node.Size;
                    frame.Node.Parent.FileCount += frame.Node.FileCount;
                    frame.Node.Parent.DirectoryCount += frame.Node.DirectoryCount + 1;
                }
                continue;
            }

            if (entry is null) continue;
            visited++;
            try
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skippedReparse++;
                    continue;
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    var childNode = CreateDirectoryNode(childDirectory, frame.Node);
                    frame.Node.Children.Add(childNode);
                    directories++;
                    stack.Push(new DirectoryFrame(childDirectory, childNode));
                }
                else if (entry is FileInfo file)
                {
                    var length = file.Length;
                    var childNode = new ScanNode
                    {
                        Name = file.Name,
                        Path = file.FullName,
                        IsDirectory = false,
                        Size = length,
                        FileCount = 1,
                        LastWriteUtc = SafeLastWriteUtc(file),
                        Attributes = file.Attributes,
                        Parent = frame.Node
                    };
                    frame.Node.Children.Add(childNode);
                    frame.Node.Size += length;
                    frame.Node.FileCount++;
                    largestFiles.Add(childNode);
                    cleanup.Consider(file.FullName, length, childNode.LastWriteUtc);
                    files++;
                    bytesFound += length;
                }
            }
            catch (Exception ex) when (IsExpectedFileSystemError(ex))
            {
                issues.Add(new ScanIssue { Path = entry.FullName, Message = ex.Message });
            }

            if (visited % 750 == 0)
            {
                progress?.Report(new ScanProgress
                {
                    CurrentPath = entry.FullName,
                    FileCount = files,
                    DirectoryCount = directories,
                    BytesFound = bytesFound
                });
            }
        }

        timer.Stop();
        largestFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (largestFiles.Count > 5000)
            largestFiles.RemoveRange(5000, largestFiles.Count - 5000);

        progress?.Report(new ScanProgress
        {
            CurrentPath = normalizedRoot,
            FileCount = files,
            DirectoryCount = directories,
            BytesFound = bytesFound
        });

        return new ScanResult
        {
            Root = root,
            LargestFiles = largestFiles,
            Suggestions = cleanup.Build(),
            Issues = issues,
            Duration = timer.Elapsed,
            FileCount = files,
            DirectoryCount = directories,
            SkippedReparsePoints = skippedReparse
        };
    }

    private static ScanNode CreateDirectoryNode(DirectoryInfo directory, ScanNode? parent) => new()
    {
        Name = string.IsNullOrWhiteSpace(directory.Name) ? directory.FullName : directory.Name,
        Path = directory.FullName,
        IsDirectory = true,
        LastWriteUtc = SafeLastWriteUtc(directory),
        Attributes = SafeAttributes(directory),
        Parent = parent
    };

    private static void FinishDirectory(ScanNode node) =>
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));

    private static DateTime SafeLastWriteUtc(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch { return DateTime.MinValue; }
    }

    private static FileAttributes SafeAttributes(FileSystemInfo info)
    {
        try { return info.Attributes; }
        catch { return 0; }
    }

    private static bool IsExpectedFileSystemError(Exception ex) => ex is
        UnauthorizedAccessException or IOException or PathTooLongException or DirectoryNotFoundException or FileNotFoundException;

    private sealed class DirectoryFrame(DirectoryInfo directory, ScanNode node)
    {
        public DirectoryInfo Directory { get; } = directory;
        public ScanNode Node { get; } = node;
        public bool Started { get; set; }
        public IEnumerator<FileSystemInfo>? Enumerator { get; set; }
    }
}
