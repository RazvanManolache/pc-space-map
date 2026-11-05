using System.Runtime.InteropServices;
using PcSpaceMap.Services;

internal static class MetadataChecks
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code,
        IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    internal static async Task RunAsync(string suite, Action<bool, string> check)
    {
        var root = Path.Combine(suite, "native-metadata");
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        var file = Path.Combine(root, "regular-é😀.dat");
        File.WriteAllBytes(file, new byte[321]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-180));
        var hidden = Path.Combine(root, "hidden.dat");
        File.WriteAllBytes(hidden, new byte[23]);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var sparsePath = Path.Combine(root, "sparse.dat");
        using (var sparse = new FileStream(sparsePath, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            if (!DeviceIoControl(sparse.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Exception("Cannot create batched sparse fixture.");
            sparse.SetLength(64 * 1024 * 1024);
            sparse.Write(new byte[4096]);
        }
        var literal = @"\\?\" + Path.Combine(root, "nul");
        File.WriteAllBytes(literal, new byte[7]);
        try
        {
            // Direct call proves the native path works, without the scanner's fallback.
            var entries = WindowsVolume.DirectoryEntries(root, default).ToArray();
            check(entries.Length == 5 && entries.All(entry =>
            {
                var individual = WindowsVolume.ReadEntry(entry.Path);
                return entry.Size == individual.Size && entry.Allocated == individual.Allocated
                    && entry.Identity == individual.Identity && entry.Modified == individual.Modified
                    && entry.Attributes == individual.Attributes && entry.IsDirectory == individual.IsDirectory;
            }), "batched Windows metadata matches individual queries for hidden, Unicode, reserved, sparse and directory entries");
            var fallback = await new DiskScanner { DatabasePath = Path.Combine(suite, "metadata-legacy.sqlite"), PreferNtfs = false, BatchDirectoryMetadata = false }.ScanAsync(root, null, default);
            var batch = await new DiskScanner { DatabasePath = Path.Combine(suite, "metadata-batch.sqlite"), PreferNtfs = false }.ScanAsync(root, null, default);
            check(fallback.Root.Size == batch.Root.Size && fallback.Root.AllocatedSize == batch.Root.AllocatedSize
                && fallback.Root.UniqueAllocatedSize == batch.Root.UniqueAllocatedSize
                && fallback.FileCount == batch.FileCount && fallback.DirectoryCount == batch.DirectoryCount,
                "batched and per-file scanners produce identical logical, allocated and unique totals");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                WindowsVolume.DirectoryEntries(root, cancelled.Token).ToArray();
                check(false, "native directory cancellation");
            }
            catch (OperationCanceledException) { check(true, "native batch enumeration supports cancellation"); }
        }
        finally { File.Delete(literal); }
    }
}
