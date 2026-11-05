using System.ComponentModel;
using System.IO;
using System.ServiceProcess;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PcSpaceMap.Services;

namespace PcSpaceMap;

internal sealed record ServiceDriveStatus(string Drive, string State, string Caption, string Tone, string Detail, int? Percent);

public partial class MainWindow
{
    private DispatcherTimer? _serviceFooterTimer;
    private readonly CancellationTokenSource _serviceFooterLifetime = new();
    private bool _refreshingServiceFooter;
    private ServiceFooter _serviceFooter = new("", "");

    private void StartServiceFooter()
    {
        if (_serviceFooterTimer is not null) return;
        _serviceFooterTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _serviceFooterTimer.Tick += ServiceFooterTick;
        _serviceFooterTimer.Start();
        _ = RefreshServiceFooterAsync();
    }

    private void StopServiceFooter()
    {
        _serviceFooterTimer?.Stop();
        _serviceFooterLifetime.Cancel();
        _serviceFooterLifetime.Dispose();
    }

    private async void ServiceFooterTick(object? sender, EventArgs e)
    {
        if (IsVisible) await RefreshServiceFooterAsync();
    }

    private async Task RefreshServiceFooterAsync()
    {
        if (_refreshingServiceFooter || _exitRequested) return;
        _refreshingServiceFooter = true;
        var token = _serviceFooterLifetime.Token;
        try
        {
            var footer = await Task.Run(() => ReadServiceFooterAsync(token), token);
            if (token.IsCancellationRequested || (_serviceFooter.Text == footer.Text && _serviceFooter.Details == footer.Details
                && _serviceFooter.WorkerVersion == footer.WorkerVersion && _serviceFooter.Installed == footer.Installed)) return;
            _serviceFooter = footer;
            Title = footer.WorkerVersion is { } worker
                ? $"PC Space Map {App.AppVersion} · Service {worker}"
                : $"PC Space Map {App.AppVersion}";
            foreach (var workspace in GetWorkspaces()) UpdateWorkspaceServiceFooter(workspace);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _refreshingServiceFooter = false; }
    }

    private void UpdateWorkspaceServiceFooter(Controls.ScanWorkspaceControl workspace) =>
        workspace.SetServiceFooter(_serviceFooter.Heading ?? _serviceFooter.Text, _serviceFooter.Details,
            _serviceFooter.Installed, _serviceFooter.Drives ?? []);

    private static async Task<ServiceFooter> ReadServiceFooterAsync(CancellationToken token)
    {
        ServiceControllerStatus status;
        try
        {
            using var controller = new ServiceController(ServiceProtocol.Name);
            status = controller.Status;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 })
        { return new("", ""); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        { return new("Windows service status unavailable", ex.Message); }

        var state = status switch
        {
            ServiceControllerStatus.StartPending => "starting",
            ServiceControllerStatus.StopPending => "stopping",
            ServiceControllerStatus.ContinuePending => "resuming",
            ServiceControllerStatus.PausePending => "pausing",
            _ => status.ToString().ToLowerInvariant()
        };
        var service = "Windows service " + state;
        var workerVersion = ServicePreferences.ReadWorkerVersion();
        var options = ServicePreferences.ReadSettings();
        if (options?.Volumes is null) return new(service, "Service drive settings are unavailable.", workerVersion, true);
        var running = status == ServiceControllerStatus.Running;
        var monitoring = running ? options.Live ? "Live monitoring on" : "Live monitoring off" : "Monitoring inactive";
        var rows = new List<ServiceDriveStatus>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        foreach (var (drive, volume) in options.Volumes.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!ServiceProtocol.IsDriveRoot(drive) || string.IsNullOrWhiteSpace(volume)) continue;
            ServiceProtocol.Response? response = null;
            if (running && !deadline.IsCancellationRequested)
            {
                try { response = await ServiceProtocol.CallAsync(new("status", drive), deadline.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            rows.Add(DescribeDrive(drive, volume, running, response));
        }
        var groups = rows.GroupBy(row => row.State).Select(group => $"{group.Key} {string.Join(", ", group.Select(row => row.Drive))}");
        return new(string.Join(" · ", new[] { service, monitoring }.Concat(groups)),
            string.Join(Environment.NewLine, new[] { service, monitoring }.Concat(rows.Select(row => row.Detail))), workerVersion, true,
            $"Service {state} · {(running && options.Live ? "Live on" : "Live off")}", rows);
    }

    private static ServiceDriveStatus DescribeDrive(string drive, string volume, bool running, ServiceProtocol.Response? response)
    {
        var letter = drive[..2];
        var indexed = response is { Snapshot: > 0 };
        var state = response switch
        {
            { Managed: false } => "Unavailable",
            { Running: true, Progress.Phase: ServiceProtocol.WaitingPhase } => indexed ? "Indexed, update queued" : "Queued",
            { Running: true, Progress.Phase: ServiceProtocol.PriorityWaitingPhase } => "Preparing priority scan",
            { Running: true } => indexed ? "Indexed, updating" : "Indexing",
            { Error: not null } => indexed ? "Indexed, update failed" : "Index failed",
            { Snapshot: > 0 } => "Indexed",
            not null => "Not indexed",
            _ => OfflineDriveState(drive, volume, running)
        };
        var progress = response?.Progress;
        var percent = response is { Running: true } ? progress?.PhasePercent : null;
        var (caption, tone) = state switch
        {
            "Indexed" => ("Indexed", "Ready"),
            "Saved index" => ("Saved", "Ready"),
            "Indexed, update queued" => ("Queued", "Queued"),
            "Queued" => ("Queued", "Queued"),
            "Preparing priority scan" => ("Priority", "Active"),
            "Indexed, updating" => ("Updating", "Active"),
            "Indexing" => ("Scanning", "Active"),
            "Indexed, update failed" or "Index failed" => ("Failed", "Problem"),
            "Not indexed" => ("Not indexed", "Neutral"),
            _ => ("Unavailable", "Problem")
        };
        if (percent is int value) caption += $" {value}%";
        var detail = $"{letter} {state}";
        if (response?.Error is { } error) detail += $" · {error}";
        else if (progress is not null)
        {
            detail += $" · {progress.Phase}";
            if (percent.HasValue) detail += $" · {percent}% of this phase";
            if (progress.FileCount > 0) detail += $" · {progress.FileCount:N0} files processed";
        }
        return new(letter, state, caption, tone, detail, percent);
    }

    private static string OfflineDriveState(string drive, string volume, bool running)
    {
        if (running) return "Status unavailable";
        try
        {
            var database = ServiceProtocol.DatabasePath(ServiceProtocol.InstallPath, drive, volume);
            return InventoryStore.ServiceSnapshotId(drive, database) > 0 ? "Saved index" : "Not indexed";
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException or InvalidOperationException)
        { return "Saved index unavailable"; }
    }

    private sealed record ServiceFooter(string Text, string Details, Version? WorkerVersion = null, bool Installed = false,
        string? Heading = null, IReadOnlyList<ServiceDriveStatus>? Drives = null);
}
