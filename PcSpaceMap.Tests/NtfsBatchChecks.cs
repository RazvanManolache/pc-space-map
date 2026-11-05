using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using PcSpaceMap.Models;
using PcSpaceMap.Services;

internal static class NtfsBatchChecks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int size, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    private static void Junction(string link, string target)
    {
        Directory.CreateDirectory(link);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
        using var handle = CreateFileW(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Cannot create the junction fixture: " + Marshal.GetLastWin32Error());
    }
    private static bool Stage(SqliteConnection connection, string root, CancellationToken token, IProgress<ScanProgress>? progress)
    {
        // Supply actual Windows file IDs through a disposable MFT-shaped table.
        // Root record 5 is deliberately absent, as it can be in real enumeration.
        InventoryStore.Execute(connection, "CREATE TEMP TABLE mft(id INTEGER PRIMARY KEY,parent INTEGER,name TEXT,attributes INTEGER)");
        using var insert = InventoryStore.Command(connection, "INSERT OR REPLACE INTO mft VALUES($id,$parent,$name,$attrs)",
            ("$id", 0L), ("$parent", 0L), ("$name", ""), ("$attrs", 0L));
        long Id(InventoryEntry entry) => (long)(ulong.Parse(entry.Identity!.Split(':')[1], NumberStyles.HexNumber) & 0x0000FFFFFFFFFFFF);
        var queue = new Queue<string>(); queue.Enqueue(root);
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = queue.Dequeue();
            var parent = directory == root ? 5 : Id(WindowsVolume.ReadEntry(directory));
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var entry = WindowsVolume.ReadEntry(path);
                insert.Parameters[0].Value = Id(entry); insert.Parameters[1].Value = parent;
                insert.Parameters[2].Value = entry.Name; insert.Parameters[3].Value = (long)entry.Attributes; insert.ExecuteNonQuery();
                if (entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0) queue.Enqueue(path);
            }
        }
        InventoryStore.Execute(connection, "CREATE INDEX mft_parent ON mft(parent)");
        return true;
    }
    internal static async Task RunAsync(string suite, Action<bool, string> check)
    {
        var root = Path.Combine(suite, "ntfs-batch");
        Directory.CreateDirectory(Path.Combine(root, "a")); Directory.CreateDirectory(Path.Combine(root, "b"));
        var file = Path.Combine(root, "a", "file.dat");
        File.WriteAllBytes(file, new byte[2000]);
        if (!Native.CreateHardLinkW(Path.Combine(root, "b", "alias.dat"), file, IntPtr.Zero)) throw new Exception("Cannot create NTFS batch alias.");
        for (var i = 0; i < 4200; i++) File.WriteAllBytes(Path.Combine(root, i + ".empty"), []);
        var target = Path.Combine(suite, "junction-target");
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        File.WriteAllBytes(Path.Combine(target, "outside.dat"), new byte[9999]);
        File.WriteAllBytes(Path.Combine(target, "nested", "outside.dat"), new byte[9999]);
        var junction = Path.Combine(root, "junction"); Junction(junction, target);
        try
        {
            var batched = new DiskScanner { DatabasePath = Path.Combine(suite, "ntfs-batched.sqlite"), NtfsStageOverride = Stage };
            var progress = new List<ScanProgress>();
            var first = await batched.ScanAsync(root, new InlineProgress(progress.Add), default);
            var perFile = await new DiskScanner { DatabasePath = Path.Combine(suite, "ntfs-per-file.sqlite"), NtfsStageOverride = Stage, BatchDirectoryMetadata = false }.ScanAsync(root, null, default);
            check(first.ScanMethod == "NTFS batched metadata scan" && first.FileCount == 4202 && first.DirectoryCount == 3
                && first.Root.Size == 4000 && first.Root.Size == perFile.Root.Size
                && first.Root.UniqueAllocatedSize == perFile.Root.UniqueAllocatedSize && first.SkippedReparsePoints == 1
                && File.Exists(Path.Combine(target, "outside.dat")),
                "NTFS directory batches preserve every hard-link name, skip junction targets and drain bounded output channels");
            var folderProgress = progress.Where(item => item.Phase == "Reading folder metadata").ToArray();
            check(folderProgress.Length >= 2 && folderProgress.All(item => item.TotalUnits == 4)
                && folderProgress[0].PhasePercent == 0 && folderProgress[^1].PhasePercent == 100
                && progress.Single(item => item.Phase == "Calculate folder totals and hard links").PhasePercent is null,
                "NTFS progress counts staged directories and clears the percentage in phases without a total");
            var again = await batched.ScanAsync(root, null, default);
            check(again.Work is { VersionsWritten: 0, FoldersRecalculated: 0 } && again.Root.Size == first.Root.Size,
                "repeated NTFS batches use fresh directory timestamps and reuse unchanged snapshot records");
            var outside = Path.Combine(junction, "nested", "outside.dat");
            try
            {
                WindowsVolume.ReadEntry(outside, verifyPath: true);
                check(false, "verified metadata rejects paths through a junction");
            }
            catch (IOException) { check(true, "verified metadata rejects paths through a junction"); }
            try
            {
                await batched.RefreshAsync(again, Path.GetDirectoryName(outside), null, default);
                check(false, "a scoped refresh cannot import a junction target");
            }
            catch (IOException)
            {
                check((await batched.LoadSavedAsync(root))!.SnapshotId == again.SnapshotId,
                    "a scoped refresh cannot import a junction target or replace the saved scan");
            }
            await CheckJournalFolderRecovery(suite, target, check);
            using var cancel = new CancellationTokenSource();
            try
            {
                await batched.ScanAsync(root, new InlineProgress(p => { if (p.FileCount > 0) cancel.Cancel(); }), cancel.Token);
                check(false, "NTFS batch cancellation");
            }
            catch (OperationCanceledException)
            {
                check((await batched.LoadSavedAsync(root))!.SnapshotId == again.SnapshotId,
                    "NTFS batch cancellation stops workers and preserves the completed snapshot");
            }
        }
        finally { Directory.Delete(junction); }
    }

    private static async Task CheckJournalFolderRecovery(string suite, string outside, Action<bool, string> check)
    {
        var root = Path.Combine(suite, "journal-folder-recovery");
        var affected = Path.Combine(root, "affected");
        Directory.CreateDirectory(affected);
        var file = Path.Combine(affected, "example.dat");
        var junction = Path.Combine(affected, "redirected");
        File.WriteAllBytes(file, new byte[10]);
        Junction(junction, outside);
        try
        {
            static string Identity(string path) => WindowsVolume.ReadEntry(path).Identity!;
            static ulong Id(string path) => Convert.ToUInt64(Identity(path).Split(':')[1], 16);
            var serial = Identity(root).Split(':')[0];
            var journal = new TestJournal { Head = new(Path.GetPathRoot(root)!, serial, 1, 10, 0) };
            var scanner = new DiskScanner { DatabasePath = Path.Combine(suite, "journal-folder.sqlite"), Journal = journal,
                PreferNtfs = false, RequireJournalForDriveRefresh = true };
            var before = await scanner.ScanAsync(root, null, default);
            File.WriteAllBytes(file, new byte[70]);
            File.WriteAllBytes(Path.Combine(root, "unrelated.dat"), new byte[100]);
            journal.Head = journal.Head with { NextUsn = 20 };
            var fileId = Id(file);
            journal.Events = [new(fileId, Id(affected), 1, Path.GetFileName(file))];
            journal.Paths[fileId] = Path.Combine(junction, "nested", "outside.dat");
            var after = await scanner.RefreshAsync(before, null, null, default);
            check(after.ScanMethod == "NTFS journal update" && after.Root.Size == 70 && before.Root.Size == 10
                && after.Issues.Any(issue => issue.Path == affected && issue.Message.StartsWith("Refreshed this folder")),
                "a journal entry through a junction refreshes only its verified ancestor and preserves the previous snapshot");
            journal.Head = journal.Head with { NextUsn = 30 };
            journal.Events = [journal.Events[0] with { Reason = 0x00010000 }];
            try
            {
                await scanner.RefreshAsync(after, null, null, default);
                check(false, "a local rescan cannot resolve an unverified hard-link change");
            }
            catch (JournalRefreshException)
            {
                check((await scanner.LoadSavedAsync(root))!.SnapshotId == after.SnapshotId,
                    "an unverified hard-link change retains the previous snapshot for broader recovery");
            }
        }
        finally { Directory.Delete(junction); }
    }
}
