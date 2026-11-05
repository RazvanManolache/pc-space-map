using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PcSpaceMap.Models;

namespace PcSpaceMap.Services;

public static class ServiceProtocol
{
    public const string Name = "PCSpaceMapIndex";
    public const string PipeName = "PCSpaceMap.Index.v2";
    public const int Version = 2;
    public const int MaxScanSlots = 8;
    public const string WaitingPhase = "Waiting for the index service";
    public const string PriorityWaitingPhase = "Preparing priority scan";
    public static string InstallPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PC Space Map Service");
    public static string DatabasePath(string baseDirectory, string root, string volume)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(volume.ToUpperInvariant())))[..24];
        return Path.Combine(baseDirectory, "Data", char.ToUpperInvariant(root[0]) + "-" + key + ".v3.sqlite");
    }

    public static bool IsDriveRoot(string? path) =>
        path is { Length: 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    public static bool VolumeMatches(string root, string fingerprint)
    {
        if (!IsDriveRoot(root)) return false;
        try
        {
            var info = new DriveInfo(root);
            return info.IsReady && info.DriveType == DriveType.Fixed && info.DriveFormat == "NTFS"
                && string.Equals(WindowsVolume.VolumeFingerprint(root), fingerprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool TryResolveDriveScope(string path, out string drive, out string scope)
    {
        drive = scope = "";
        if (!Path.IsPathFullyQualified(path)) return false;
        try
        {
            scope = InventoryStore.Normalize(path);
            drive = Path.GetPathRoot(scope) ?? "";
            return IsDriveRoot(drive) && scope.AsSpan(drive.Length).IndexOfAny(':', '*', '?') < 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    public sealed record Request(string Operation, string Root, Guid? Job = null, string? Scope = null);
    public sealed record Settings(string ClientSid, Dictionary<string, string> Volumes, bool Live = true,
        bool AutoStart = true, int MaxConcurrentScans = 1);
    public sealed record Response(bool Managed, long Snapshot = 0, Guid? Job = null, bool Running = false,
        string? Error = null, ScanProgress? Progress = null, int ProtocolVersion = Version, string? Volume = null,
        bool SupportsFolderScans = false);

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > 65536) throw new InvalidDataException("Invalid service message length.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Empty service message.");
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        if (data.Length > 65536) throw new InvalidDataException("Service message too long.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, data.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(data, token);
        await stream.FlushAsync(token);
    }

    public static async Task<Response?> CallAsync(Request request, CancellationToken token)
    {
        if (!IsDriveRoot(request.Root) || !Directory.Exists(InstallPath)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        const PipeAccessRights access = PipeAccessRights.ReadData | PipeAccessRights.WriteData
            | PipeAccessRights.ReadAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;
        using var pipe = new NamedPipeClientStream(".", PipeName, access, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync(2200, deadline.Token);
            // A local process could claim the name first. Only accept the SYSTEM-owned pipe.
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner is not SecurityIdentifier sid || !sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) return null;
            await WriteAsync(pipe, request, deadline.Token);
            var response = await ReadAsync<Response>(pipe, deadline.Token);
            await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
            return response.ProtocolVersion == Version ? response : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException or JsonException)
        { return null; }
    }
}
