using System.Windows.Threading;
using PcSpaceMap.Models;
using PcSpaceMap.Services;

namespace PcSpaceMap.Controls;

public partial class ScanWorkspaceControl
{
    private bool _usingService;
    private DispatcherTimer? _serviceTimer;
    private bool _checkingService;
    private bool _workspaceClosed;
    private (ScanResult? View, long Checked) _checkedFolderSnapshot;

    private void SetBackend(bool service)
    {
        _usingService = service;
        _checkedFolderSnapshot = default;
        BackendLabel.Text = service ? "Windows service" : "Standalone";
        WatchBox.ToolTip = service ? "Manage background indexing in Windows Service." : "Live updates";
        _serviceTimer?.Stop();
        if (!service) return;
        if (_serviceTimer is null)
        {
            _serviceTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            _serviceTimer.Tick += ServiceTick;
        }
        _serviceTimer.Start();
    }

    private async void ServiceTick(object? sender, EventArgs e)
    {
        if (_checkingService || _isScanning || _batchLocked || _workspaceClosed || !PickerMatchesInventory
            || _result is not { } prior || !ServiceProtocol.TryResolveDriveScope(prior.Root.Path, out var drive, out var scope)) return;
        _checkingService = true;
        try
        {
            var status = await ServiceProtocol.CallAsync(new("status", drive), CancellationToken.None);
            if (_workspaceClosed || !_usingService || _isScanning || _batchLocked || _result != prior) return;
            BackendLabel.Text = status?.Managed == true ? "Windows service" : "Service offline · saved view";
            if (status is not { Managed: true, Snapshot: > 0, Volume: not null }) return;
            if (prior.Inventory is { } inventory && string.Equals(inventory.DatabasePath,
                    ServiceProtocol.DatabasePath(ServiceProtocol.InstallPath, drive, status.Volume), StringComparison.OrdinalIgnoreCase))
            {
                if (status.Snapshot == prior.SnapshotId) return;
                // Reopen an unchanged view before its lazy queries can lose
                // the snapshot versions retained by the service.
                if (!string.Equals(scope, drive, StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - prior.CompletedUtc < ServiceProtocol.SnapshotHistoryGrace / 2)
                {
                    var check = (prior, status.Snapshot);
                    if (_checkedFolderSnapshot == check) return;
                    if (await Task.Run(() => inventory.FolderUnchanged(scope, prior.SnapshotId, status.Snapshot)))
                    {
                        if (_result == prior) _checkedFolderSnapshot = check;
                        return;
                    }
                }
            }
            var newer = await ServiceInventoryClient.OpenSnapshotAsync(drive, scope, status);
            if (_workspaceClosed || !_usingService || _isScanning || _batchLocked || !PickerMatchesInventory || _result != prior || newer is null) return;
            var view = Treemap.Root?.Path;
            var selection = _selectedNode?.Path;
            _result = newer;
            _checkedFolderSnapshot = default;
            ShowResult(newer, "Service inventory updated.", preserveView: true);
            RestoreNavigation(view, selection);
        }
        catch (Exception ex)
        {
            if (!_workspaceClosed) BackendLabel.Text = "Service index unavailable: " + ex.Message;
        }
        finally { _checkingService = false; }
    }
}
