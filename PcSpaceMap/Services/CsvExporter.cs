using System.IO;
using System.Text;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

public static class CsvExporter
{
    public static Task ExportAsync(ScanResult result, string destination, CancellationToken cancellationToken = default) =>
        ExportNodesAsync(result.Inventory?.Enumerate(result.SnapshotId, result.Root.Path) ?? result.Root.DescendantsAndSelf(), destination, cancellationToken);

    public static async Task ExportAsync(ScanNode root, string destination, CancellationToken cancellationToken = default)
        => await ExportNodesAsync(root.DescendantsAndSelf(), destination, cancellationToken);

    private static async Task ExportNodesAsync(IEnumerable<ScanNode> nodes, string destination, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Type,Path,SizeBytes,Size,LastModified,FilesBelow,FoldersBelow,AllocatedBytes,UniqueAllocatedBytes");

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = string.Join(',',
                Csv(node.IsDirectory ? "Folder" : "File"),
                Csv(node.Path),
                node.Size,
                Csv(node.SizeText),
                Csv(node.ModifiedText),
                node.FileCount,
                node.DirectoryCount,
                node.AllocatedSize?.ToString() ?? "",
                node.UniqueAllocatedSize?.ToString() ?? "");
            await writer.WriteLineAsync(line);
        }
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
