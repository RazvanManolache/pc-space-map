using System.IO;
using PcSpaceMap.Models;
using PcSpaceMap.Services;

namespace PcSpaceMap;

internal static class ServiceInventoryClient
{
    internal static async Task<ScanResult?> ScanAsync(string path, string operation, IProgress<ScanProgress> progress,
        CancellationToken token, string? folderToRefresh = null, bool openPublished = false)
    {
        if (!ServicePreferences.IsInstalled()) return null;
        var route = Resolve(path);
        var scope = folderToRefresh ?? route.Folder;
        if (scope is not null && string.Equals(InventoryStore.Normalize(scope), route.Drive, StringComparison.OrdinalIgnoreCase))
            scope = null;
        if (openPublished)
        {
            try
            {
                var published = await OpenStoredSnapshotAsync(route);
                token.ThrowIfCancellationRequested();
                if (published is not null) return published;
            }
            catch (DirectoryNotFoundException) when (route.Folder is not null)
            {
                // The drive is indexed, but a newly created folder may still need a targeted scan.
            }
        }
        var status = await CallWithRetryAsync(new("status", route.Drive), token);
        if (status is null)
            throw new IOException("The installed indexing service is not responding. Start or check it in Windows Service.");
        if (!status.Managed)
            throw new IOException($"The service cannot index {route.Drive}. Check the configured drives and volume in Windows Service.");
        if (scope is not null && !status.SupportsFolderScans)
            throw new IOException("Upgrade the indexing service in Windows Service to scan or refresh individual folders.");

        var job = Guid.NewGuid();
        try
        {
            var response = await CallWithRetryAsync(new(operation, route.Drive, job, scope), token)
                ?? throw new IOException("The service disconnected before accepting the scan.");
            while (response.Running)
            {
                if (response.Job != job) throw new IOException("The service returned a different job.");
                if (response.Progress is not null) progress.Report(response.Progress);
                await Task.Delay(750, token);
                response = await CallWithRetryAsync(new("status", route.Drive, job), token)
                    ?? throw new IOException("The indexing service disconnected. Check it in Windows Service and try again.");
            }
            token.ThrowIfCancellationRequested();
            if (response.Error is not null) throw new IOException(response.Error);
            if (!response.Managed || response.Job != job) throw new IOException("The service did not complete the requested job.");
            return await OpenSnapshotAsync(route.Drive, route.Folder, response)
                ?? throw new IOException("The service completed without publishing an inventory.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await ServiceProtocol.CallAsync(new("cancel", route.Drive, job), CancellationToken.None);
            throw;
        }
    }

    internal static async Task<(bool UsesService, ScanResult? Result)> OpenSavedAsync(string path)
    {
        if (!ServicePreferences.IsInstalled()) return (false, null);
        var route = Resolve(path);
        var status = await ServiceProtocol.CallAsync(new("status", route.Drive), CancellationToken.None);
        if (status is { Managed: false })
            throw new IOException($"The service cannot access its configured volume at {route.Drive}.");
        if (status is { Snapshot: > 0 }) return (true, await OpenSnapshotAsync(route.Drive, route.Folder, status));

        // The service may be stopped or still opening a previously completed database.
        return (true, await OpenStoredSnapshotAsync(route));
    }

    private static Task<ScanResult?> OpenStoredSnapshotAsync((string Drive, string? Folder, string Volume) route) =>
        Task.Run(() => ServiceProtocol.VolumeMatches(route.Drive, route.Volume)
            ? InventoryStore.OpenServiceSnapshot(route.Drive, ServiceProtocol.DatabasePath(ServiceProtocol.InstallPath, route.Drive, route.Volume),
                folder: route.Folder)
            : null);

    internal static Task<ScanResult?> OpenSnapshotAsync(string drive, string? folder, ServiceProtocol.Response response) =>
        response is { Snapshot: > 0, Volume: not null }
            ? Task.Run(() => InventoryStore.OpenServiceSnapshot(drive,
                ServiceProtocol.DatabasePath(ServiceProtocol.InstallPath, drive, response.Volume), response.Snapshot, folder))
            : Task.FromResult<ScanResult?>(null);

    internal static (string Drive, string? Folder, string Volume) Resolve(string path)
    {
        if (!ServiceProtocol.TryResolveDriveScope(path, out var drive, out var scope))
            throw new IOException("The installed indexing service accepts folders on configured local NTFS drives.");
        var settings = ServicePreferences.ReadSettings();
        if (settings?.Volumes is null)
            throw new IOException("The installed service's drive settings are unavailable for this Windows account.");
        var volume = settings.Volumes.FirstOrDefault(pair => pair.Key.Equals(drive, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(volume))
            throw new IOException($"{drive} is not configured for the indexing service. Add it in Windows Service.");
        return (drive, scope.Equals(drive, StringComparison.OrdinalIgnoreCase) ? null : scope, volume);
    }

    private static async Task<ServiceProtocol.Response?> CallWithRetryAsync(ServiceProtocol.Request request, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await ServiceProtocol.CallAsync(request, token) is { } response) return response;
            if (attempt < 2) await Task.Delay(250, token);
        }
        return null;
    }
}
