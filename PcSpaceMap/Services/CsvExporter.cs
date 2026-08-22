using System.IO;
using System.Text;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

public static class CsvExporter
{
    public static async Task ExportAsync(ScanNode root, string destination, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Type,Path,SizeBytes,Size,LastModified,FilesBelow,FoldersBelow");

        foreach (var node in root.DescendantsAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = string.Join(',',
                Csv(node.IsDirectory ? "Folder" : "File"),
                Csv(node.Path),
                node.Size,
                Csv(node.SizeText),
                Csv(node.ModifiedText),
                node.FileCount,
                node.DirectoryCount);
            await writer.WriteLineAsync(line);
        }
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
