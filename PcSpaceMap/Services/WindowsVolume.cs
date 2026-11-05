using System.ComponentModel;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace PcSpaceMap.Services;

public sealed record JournalCursor(string Volume, string VolumeIdentity, ulong JournalId, long NextUsn, long FirstUsn);
internal sealed record VolumeChange(ulong FileId, ulong ParentId, uint Reason, string Name);

internal interface IVolumeJournal
{
    JournalCursor? Cursor(string root);
    IReadOnlyList<VolumeChange> Changes(JournalCursor saved, JournalCursor head, CancellationToken token);
    string? ResolveId(string volume, ulong fileId);
}

internal sealed class WindowsJournal : IVolumeJournal
{
    public JournalCursor? Cursor(string root) => WindowsVolume.Cursor(root);
    public IReadOnlyList<VolumeChange> Changes(JournalCursor saved, JournalCursor head, CancellationToken token) => WindowsVolume.Changes(saved, head, token);
    public string? ResolveId(string volume, ulong fileId) => WindowsVolume.ResolveId(volume, fileId);
}

// Adapted from SuperSearch's MFT/USN strategy: volume enumeration plus per-entry metadata,
// with ordinary traversal when volume access is unavailable. File contents are never read.
internal static class WindowsVolume
{
    private const ulong FrnIndexMask = 0x0000FFFFFFFFFFFF;
    private const uint QueryJournal = 0x900F4, EnumUsn = 0x900B3, ReadJournal = 0x900BB;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out StandardInformation info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadCompressionInformation(SafeFileHandle handle, int infoClass, out CompressionInformation info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadDirectoryInformation(SafeFileHandle handle, int infoClass, [Out] byte[] buffer, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileAttributesExW(string path, int infoLevel, out AttributeInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder? name, int nameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder? fs, int fsSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor id, uint access, uint share, IntPtr security, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileNameW(string path, uint flags, ref uint length, StringBuilder name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileNameW(IntPtr handle, ref uint length, StringBuilder name);
    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public ulong Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInformation
    {
        public long AllocationSize, EndOfFile;
        public uint Links;
        public byte DeletePending, Directory;
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CompressionInformation
    {
        [FieldOffset(0)] public long Size;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint SizeHigh, SizeLow;
    }

    private static SafeFileHandle OpenVolume(string root, uint access = 0x80000000) =>
        CreateFileW(@"\\.\" + Path.GetPathRoot(root)![..2], access, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskExtent
    {
        public uint DiskNumber;
        public long StartingOffset;
        public long ExtentLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VolumeDiskExtents
    {
        public uint Count;
        public DiskExtent First;
    }

    internal static uint[] BackingDisks(string root)
    {
        if (!ServiceProtocol.IsDriveRoot(root)) return [];
        using var handle = OpenVolume(root, access: 0);
        if (handle.IsInvalid) return [];

        const uint getVolumeDiskExtents = 0x00560000;
        var offset = Marshal.OffsetOf<VolumeDiskExtents>(nameof(VolumeDiskExtents.First)).ToInt32();
        var stride = Marshal.SizeOf<DiskExtent>();
        var buffer = new byte[offset + stride];
        while (true)
        {
            var ok = DeviceIoControl(handle, getVolumeDiskExtents, null, 0, buffer, buffer.Length, out var read, IntPtr.Zero);
            var count = BitConverter.ToUInt32(buffer, 0);
            if (count is 0 or > 4096) return [];
            var size = offset + (int)count * stride;
            if (ok)
            {
                if (size > read) return [];
                return Enumerable.Range(0, (int)count).Select(i => BitConverter.ToUInt32(buffer, offset + i * stride))
                    .Distinct().Order().ToArray();
            }
            if (Marshal.GetLastWin32Error() != 234 || size <= buffer.Length) return [];
            buffer = new byte[size];
        }
    }
    private static string? Serial(string root)
    {
        return GetVolumeInformationW(Path.GetPathRoot(root)!, null, 0, out var serial, out _, out _, null, 0) ? serial.ToString("X8") : null;
    }
    internal static string? VolumeFingerprint(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is null) return null;
        var serial = Serial(root);
        return serial is null ? null : root.ToUpperInvariant() + "|" + serial;
    }

    public static JournalCursor? Cursor(string root)
    {
        if (!OperatingSystem.IsWindows() || root.StartsWith(@"\\") || Path.GetPathRoot(root)?.Length != 3) return null;
        using var handle = OpenVolume(root);
        if (handle.IsInvalid) return null;
        var buffer = new byte[80];
        if (!DeviceIoControl(handle, QueryJournal, null, 0, buffer, buffer.Length, out var size, IntPtr.Zero) || size < 56) return null;
        var serial = Serial(root);
        return serial is null ? null : new(Path.GetPathRoot(root)!, serial, BitConverter.ToUInt64(buffer, 0), BitConverter.ToInt64(buffer, 16), BitConverter.ToInt64(buffer, 8));
    }

    private static string VerifyOpenedPath(SafeFileHandle handle, string requested, uint serial, uint? requiredSerial)
    {
        if (requiredSerial.HasValue && requiredSerial != serial) throw new IOException("The entry is on a different volume.");
        var actual = new StringBuilder(Math.Max(260, requested.Length + 10));
        var length = GetFinalPathNameByHandleW(handle, actual, (uint)actual.Capacity, 0);
        if (length >= actual.Capacity && length < 32768)
        {
            actual.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, actual, (uint)actual.Capacity, 0);
        }
        if (length == 0 || length >= actual.Capacity)
            throw new IOException("Could not verify the opened path: " + requested);
        static string NormalPath(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
        var observed = actual.ToString();
        if (!NormalPath(observed).TrimEnd('\\').Equals(NormalPath(requested).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The opened path resolves through a link or moved directory: " + requested);
        return requested.StartsWith(@"\\?\", StringComparison.Ordinal) ? observed : NormalPath(observed);
    }

    public static InventoryEntry ReadEntry(string path, bool attributesOnly = false, bool verifyPath = false, uint? requiredSerial = null)
    {
        // MFT and directory enumeration already supply absolute paths. GetFullPath would
        // reinterpret literal NTFS filenames such as "nul" as Win32 device paths.
        var normalized = Path.IsPathFullyQualified(path) ? path.Replace('/', '\\') : InventoryStore.Normalize(path);
        var nativePath = normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ? normalized
            : normalized.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + normalized[2..] : @"\\?\" + normalized;
        FileInformation basic = default;
        var knownIdentity = false;
        using var handle = attributesOnly ? new SafeFileHandle(new IntPtr(-1), ownsHandle: false)
            : CreateFileW(nativePath, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        var openError = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (!handle.IsInvalid) knownIdentity = GetFileInformationByHandle(handle, out basic);
        if (verifyPath || requiredSerial.HasValue)
        {
            if (openError is 2 or 3) throw new FileNotFoundException("The entry no longer exists.", normalized);
            if (!knownIdentity) throw new IOException("Could not open the entry for path verification: " + normalized);
            normalized = VerifyOpenedPath(handle, normalized, basic.VolumeSerial, requiredSerial);
        }
        if (!knownIdentity)
        {
            if (!GetFileAttributesExW(nativePath, 0, out var fallback))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) throw new FileNotFoundException("The entry no longer exists.", normalized);
                if (error == 5) throw new UnauthorizedAccessException("Access denied while reading metadata: " + normalized);
                throw new IOException(new Win32Exception(error).Message + ": " + normalized);
            }
            basic.Attributes = fallback.Attributes;
            basic.Write = fallback.Write;
            basic.SizeHigh = fallback.SizeHigh;
            basic.SizeLow = fallback.SizeLow;
        }
        var attributes = (FileAttributes)basic.Attributes;
        var dir = (attributes & FileAttributes.Directory) != 0;
        long? allocated = dir ? 0 : null;
        string? identity = knownIdentity && (basic.IndexHigh != 0 || basic.IndexLow != 0)
            ? $"{basic.VolumeSerial:X8}:{(((ulong)basic.IndexHigh << 32) | basic.IndexLow):X16}" : null;
        if (!dir && (attributes & FileAttributes.ReparsePoint) == 0 && knownIdentity)
        {
            if (GetFileInformationByHandleEx(handle, 1, out var standard, 24)) allocated = standard.AllocationSize;
            if ((attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0)
            {
                if (ReadCompressionInformation(handle, 8, out var compressed, 16)) allocated = compressed.Size;
            }
        }
        var write = ((long)basic.Write.dwHighDateTime << 32) | (uint)basic.Write.dwLowDateTime;
        DateTime modified;
        try { modified = DateTime.FromFileTimeUtc(write); }
        catch (ArgumentOutOfRangeException) { modified = DateTime.MinValue; }
        var name = Path.GetFileName(normalized);
        return new(normalized, Path.GetDirectoryName(normalized) ?? "", string.IsNullOrEmpty(name) ? normalized : name,
            dir, dir ? 0 : checked((long)(((ulong)basic.SizeHigh << 32) | basic.SizeLow)),
            allocated, identity, modified, attributes, knownIdentity ? basic.Links : 1);
    }

    internal static IEnumerable<InventoryEntry> DirectoryEntries(string path, CancellationToken token, bool includeDirectory = false,
        bool verifyPath = false, uint? requiredSerial = null)
    {
        var nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        using var handle = CreateFileW(nativePath, 0x81, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var directory))
            throw new NotSupportedException("Batched directory metadata is unavailable: " + new Win32Exception().Message);
        if (verifyPath || requiredSerial.HasValue) path = VerifyOpenedPath(handle, path, directory.VolumeSerial, requiredSerial);
        var directoryAttributes = (FileAttributes)directory.Attributes;
        if ((directoryAttributes & FileAttributes.Directory) == 0) throw new NotSupportedException("The entry is no longer a directory.");
        if (includeDirectory)
        {
            var name = Path.GetFileName(path);
            DateTime modified;
            try { modified = DateTime.FromFileTimeUtc(((long)directory.Write.dwHighDateTime << 32) | (uint)directory.Write.dwLowDateTime); }
            catch (ArgumentOutOfRangeException) { modified = DateTime.MinValue; }
            var identity = ((ulong)directory.IndexHigh << 32) | directory.IndexLow;
            yield return new(path, Path.GetDirectoryName(path) ?? "", name.Length == 0 ? path : name, true, 0, 0,
                identity == 0 ? null : $"{directory.VolumeSerial:X8}:{identity:X16}", modified, directoryAttributes);
        }
        if ((directoryAttributes & FileAttributes.ReparsePoint) != 0) yield break;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var started = false;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // FILE_ID_BOTH_DIR_INFO, with a variable UTF-16 filename at byte 104.
                if (!ReadDirectoryInformation(handle, 10, buffer, (uint)buffer.Length))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 18) yield break; // ERROR_NO_MORE_FILES
                    if (!started) throw new NotSupportedException(new Win32Exception(error).Message);
                    throw new IOException("Directory metadata enumeration failed: " + new Win32Exception(error).Message);
                }
                started = true;
                var offset = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (offset > buffer.Length - 104) throw new IOException("Invalid directory metadata header.");
                    var next = BitConverter.ToUInt32(buffer, offset);
                    var nameBytes = BitConverter.ToUInt32(buffer, offset + 60);
                    if ((nameBytes & 1) != 0 || nameBytes == 0 || nameBytes > buffer.Length - offset - 104)
                        throw new IOException("Invalid directory metadata filename.");
                    var name = Encoding.Unicode.GetString(buffer, offset + 104, (int)nameBytes);
                    if (name is not "." and not "..")
                    {
                        var fullPath = Path.Join(path, name);
                        var attributes = (FileAttributes)BitConverter.ToUInt32(buffer, offset + 56);
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        var size = isDirectory ? 0 : BitConverter.ToInt64(buffer, offset + 40);
                        long? allocated = isDirectory ? 0 : BitConverter.ToInt64(buffer, offset + 48);
                        if (!isDirectory && (attributes & FileAttributes.ReparsePoint) == 0
                            && (attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0)
                        {
                            if (verifyPath || requiredSerial.HasValue)
                            {
                                try { allocated = ReadEntry(fullPath, verifyPath: true, requiredSerial: requiredSerial).Allocated; }
                                catch (IOException) { allocated = null; }
                            }
                            else
                            {
                                var low = GetCompressedFileSizeW(nativePath.TrimEnd('\\') + "\\" + name, out var high);
                                if (low != uint.MaxValue || Marshal.GetLastWin32Error() == 0) allocated = (long)(((ulong)high << 32) | low);
                            }
                        }
                        var id = BitConverter.ToUInt64(buffer, offset + 96);
                        DateTime modified;
                        try { modified = DateTime.FromFileTimeUtc(BitConverter.ToInt64(buffer, offset + 24)); }
                        catch (ArgumentOutOfRangeException) { modified = DateTime.MinValue; }
                        yield return new(fullPath, path, name, isDirectory, size, allocated,
                            id == 0 ? null : $"{directory.VolumeSerial:X8}:{id:X16}", modified, attributes);
                    }
                    if (next == 0) break;
                    if (next < 104 + nameBytes || next % 8 != 0 || next > buffer.Length - offset - 104)
                        throw new IOException("Invalid directory metadata record offset.");
                    offset += (int)next;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static IEnumerable<string> HardLinkPaths(string path)
    {
        uint length = 32768;
        var name = new StringBuilder((int)length);
        var nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        var handle = FindFirstFileNameW(nativePath, 0, ref length, name);
        if (handle == new IntPtr(-1)) throw new IOException("Cannot enumerate hard-link aliases for " + path + ": " + new Win32Exception().Message);
        try
        {
            while (true)
            {
                yield return Path.GetPathRoot(path)!.TrimEnd('\\') + name;
                length = 32768;
                name.Clear();
                if (!FindNextFileNameW(handle, ref length, name))
                {
                    if (Marshal.GetLastWin32Error() != 38) throw new IOException("Cannot finish hard-link enumeration: " + new Win32Exception().Message);
                    break;
                }
            }
        }
        finally { FindClose(handle); }
    }

    // Stage only compact name records in SQLite; do not retain a whole-volume name table in RAM.
    public static bool StageMft(SqliteConnection connection, string root, CancellationToken token, IProgress<PcSpaceMap.Models.ScanProgress>? progress = null)
    {
        if (!InventoryStore.Normalize(root).Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase) || Cursor(root) is null) return false;
        using var handle = OpenVolume(root);
        if (handle.IsInvalid) return false;
        InventoryStore.Execute(connection, "CREATE TEMP TABLE mft(id INTEGER PRIMARY KEY,parent INTEGER,name TEXT,attributes INTEGER);");
        using var insert = InventoryStore.Command(connection, "INSERT OR REPLACE INTO mft VALUES($id,$parent,$name,$attributes)", ("$id", 0L), ("$parent", 0L), ("$name", ""), ("$attributes", 0L));
        var input = new byte[24];
        BitConverter.GetBytes(long.MaxValue).CopyTo(input, 16);
        var output = new byte[1024 * 1024];
        long names = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!DeviceIoControl(handle, EnumUsn, input, input.Length, output, output.Length, out var size, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 38) return FinishMft(connection);
                InventoryStore.Execute(connection, "DROP TABLE mft");
                return false;
            }
            if (size <= 8) return FinishMft(connection);
            var next = BitConverter.ToUInt64(output, 0);
            if (next <= BitConverter.ToUInt64(input, 0)) throw new IOException("NTFS enumeration did not advance.");
            BitConverter.GetBytes(next).CopyTo(input, 0);
            foreach (var record in Parse(output, size))
            {
                insert.Parameters[0].Value = (long)(record.FileId & FrnIndexMask);
                insert.Parameters[1].Value = (long)(record.ParentId & FrnIndexMask);
                insert.Parameters[2].Value = record.Name;
                insert.Parameters[3].Value = (long)record.Reason; // Enumeration parser supplies attributes here.
                insert.ExecuteNonQuery();
                if (++names % 10000 == 0) progress?.Report(new() { Phase = "Enumerating NTFS names", CurrentPath = $"{root} ({names:N0} names)" });
            }
        }
    }

    private static bool FinishMft(SqliteConnection connection)
    {
        InventoryStore.Execute(connection, "CREATE INDEX mft_parent ON mft(parent)");
        return true;
    }

    public static IEnumerable<string> MftPaths(SqliteConnection connection, string root, bool directoriesOnly = false, bool includeRoot = false)
    {
        using var cmd = InventoryStore.Command(connection, $"""
            WITH RECURSIVE paths(id,path,attributes) AS (
                SELECT 5,$root,16
                UNION ALL SELECT m.id,p.path || CASE WHEN substr(p.path,-1)='\' THEN '' ELSE '\' END || m.name,m.attributes
                FROM mft m JOIN paths p ON m.parent=p.id WHERE m.id<>5 AND m.id<>m.parent AND (p.attributes & 1024)=0
                  {(directoriesOnly ? "AND (m.attributes & 16)<>0" : "")}
            ) SELECT path FROM paths WHERE path<>$root OR $include=1
            """, ("$root", root), ("$include", includeRoot ? 1 : 0));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }

    internal static long CountMftDirectories(SqliteConnection connection)
    {
        // Count the same reachable directory records as MftPaths without building their strings.
        using var command = InventoryStore.Command(connection, """
            WITH RECURSIVE dirs(id,attributes) AS (
                SELECT 5,16
                UNION ALL SELECT m.id,m.attributes FROM mft m JOIN dirs d ON m.parent=d.id
                WHERE m.id<>5 AND m.id<>m.parent AND (d.attributes & 1024)=0 AND (m.attributes & 16)<>0
            ) SELECT count(*) FROM dirs
            """);
        return (long)command.ExecuteScalar()!;
    }

    internal static IEnumerable<VolumeChange> Parse(byte[] buffer, int size, bool journal = false)
    {
        for (var offset = 8; offset + 8 <= size;)
        {
            var length = BitConverter.ToInt32(buffer, offset);
            var version = BitConverter.ToUInt16(buffer, offset + 4);
            if (length < 60 || offset + length > size || version != 2) throw new IOException("Unsupported or invalid NTFS journal record; a directory scan is required.");
            var nameLength = BitConverter.ToUInt16(buffer, offset + 56);
            var nameOffset = BitConverter.ToUInt16(buffer, offset + 58);
            if (nameOffset < 60 || nameOffset + nameLength > length || nameLength % 2 != 0) throw new IOException("Invalid NTFS name record.");
            yield return new(BitConverter.ToUInt64(buffer, offset + 8), BitConverter.ToUInt64(buffer, offset + 16),
                BitConverter.ToUInt32(buffer, offset + (journal ? 40 : 52)), Encoding.Unicode.GetString(buffer, offset + nameOffset, nameLength));
            offset += length;
        }
    }

    public static IReadOnlyList<VolumeChange> Changes(JournalCursor saved, JournalCursor head, CancellationToken token)
    {
        if (saved.VolumeIdentity != head.VolumeIdentity || saved.JournalId != head.JournalId || saved.NextUsn < head.FirstUsn || saved.NextUsn > head.NextUsn)
            throw new JournalReplayGapException("The NTFS journal was replaced or wrapped; a full scan is required.");
        using var handle = OpenVolume(saved.Volume);
        var input = new byte[40];
        BitConverter.GetBytes(saved.NextUsn).CopyTo(input, 0);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(input, 8);
        BitConverter.GetBytes(saved.JournalId).CopyTo(input, 32);
        var buffer = new byte[1024 * 1024];
        var changes = new Dictionary<(ulong, ulong, string), VolumeChange>();
        while (BitConverter.ToInt64(input, 0) < head.NextUsn)
        {
            token.ThrowIfCancellationRequested();
            if (!DeviceIoControl(handle, ReadJournal, input, input.Length, buffer, buffer.Length, out var size, IntPtr.Zero)) throw new Win32Exception();
            if (size < 8) throw new IOException("Invalid NTFS journal response.");
            foreach (var item in Parse(buffer, size, true))
            {
                var key = (item.FileId, item.ParentId, item.Name);
                changes[key] = changes.TryGetValue(key, out var earlier) && (item.Reason | earlier.Reason) != item.Reason
                    ? item with { Reason = item.Reason | earlier.Reason }
                    : item;
            }
            if (changes.Count > 100_000) throw new JournalReplayGapException("Many files changed; a full scan is required.");
            var next = BitConverter.ToInt64(buffer, 0);
            if (next <= BitConverter.ToInt64(input, 0)) throw new IOException("NTFS journal did not advance.");
            BitConverter.GetBytes(next).CopyTo(input, 0);
        }
        return changes.Values.ToList();
    }

    public static string? ResolveId(string volume, ulong fileId)
    {
        using var handle = OpenVolume(volume);
        var descriptor = new FileIdDescriptor { Size = 24, Id = fileId };
        using var file = OpenFileById(handle, ref descriptor, 0x80, 7, IntPtr.Zero, 0x02200000);
        if (file.IsInvalid) return null;
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(file, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) return null;
        var value = path.ToString();
        return value.StartsWith(@"\\?\") ? value[4..] : value;
    }
}
