using System.Windows.Threading;
using PcSpaceMap.Services;

namespace PcSpaceMap.Controls;

public partial class ScanWorkspaceControl
{
    private bool _usingService;
    private DispatcherTimer? _serviceTimer;
    private bool _checkingService;
    private bool _workspaceClosed;

    private void SetBackend(bool service)
    {
        _usingService = service;
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
            if (status.Snapshot == prior.SnapshotId && string.Equals(prior.Inventory?.DatabasePath,
                    ServiceProtocol.DatabasePath(ServiceProtocol.InstallPath, drive, status.Volume), StringComparison.OrdinalIgnoreCase)) return;
            var newer = await ServiceInventoryClient.OpenSnapshotAsync(drive, scope, status);
            if (_workspaceClosed || !_usingService || _isScanning || _batchLocked || !PickerMatchesInventory || _result != prior || newer is null) return;
            var view = Treemap.Root?.Path;
            var selection = _selectedNode?.Path;
            _result = newer;
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
