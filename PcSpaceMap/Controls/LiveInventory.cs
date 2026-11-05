using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PcSpaceMap.Controls;

public partial class ScanWorkspaceControl
{
    private const int WatchIntervalMs = 500;
    private readonly object _watchLock = new();
    private readonly string _appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCSpaceMap");
    private DispatcherTimer? _watchTimer;
    private FileSystemWatcher? _watcher;
    private string? _pendingScope;
    private long _firstChange;
    private long _lastChange;

    private void Watch_Click(object sender, RoutedEventArgs e) => SyncWatcher();

    private void SyncWatcher()
    {
        var root = _result?.Root.Path;
        if (!_usingService && !_serviceRequired && WatchBox.IsChecked == true && _watcher is not null && string.Equals(_watcher.Path, root, StringComparison.OrdinalIgnoreCase))
            return;
        StopWatcher();
        if (_usingService || _serviceRequired || WatchBox.IsChecked != true || root is null) return;
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024
            };
            watcher.Changed += WatchPathChanged;
            watcher.Created += WatchPathChanged;
            watcher.Deleted += WatchPathChanged;
            watcher.Renamed += WatchRenamed;
            watcher.Error += WatchError;
            lock (_watchLock) _watcher = watcher;
            watcher.EnableRaisingEvents = true;
            _watchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(WatchIntervalMs) };
            _watchTimer.Tick += WatchTick;
            _watchTimer.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StopWatcher();
            WatchBox.IsChecked = false;
            StatusText.Text = "Live updates unavailable: " + ex.Message;
        }
    }

    private void StopWatcher()
    {
        _watchTimer?.Stop();
        _watchTimer = null;
        FileSystemWatcher? watcher;
        lock (_watchLock)
        {
            watcher = _watcher;
            _watcher = null;
            _pendingScope = null;
        }
        watcher?.Dispose();
    }

    internal void CloseWorkspace()
    {
        if (_workspaceClosed) return;
        _workspaceClosed = true;
        UpdateScanControls();
        _serviceTimer?.Stop();
        StopWatcher();
        _treemapPreviewCancellation?.Cancel();
        _scanCancellation?.Cancel();
        _filterCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _contentsCancellation?.Cancel();
        _cleanupCancellation?.Cancel();
        _filterVersion++;
    }

    private void WatchPathChanged(object sender, FileSystemEventArgs e) => QueueWatchPath(sender, e.FullPath);

    private void WatchRenamed(object sender, RenamedEventArgs e)
    {
        QueueWatchPath(sender, e.OldFullPath);
        QueueWatchPath(sender, e.FullPath);
    }

    private void WatchError(object sender, ErrorEventArgs e) => QueueWatchPath(sender, null);

    private void QueueWatchPath(object sender, string? path)
    {
        lock (_watchLock)
        {
            if (_watcher is null || !ReferenceEquals(sender, _watcher)) return;
            var root = _watcher.Path;
            if (path is not null && IsSameOrBelow(path, _appData)
                && (!IsSameOrBelow(root, _appData) || IsSameOrBelow(path, Path.Combine(_appData, "inventories")))) return;
            path ??= root;
            var now = Environment.TickCount64;
            if (_pendingScope is null) _firstChange = now;
            else
            {
                while (!IsSameOrBelow(_pendingScope, path) && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
                    path = Path.GetDirectoryName(path) ?? root;
            }
            _pendingScope = path;
            _lastChange = now;
        }
    }

    private async void WatchTick(object? sender, EventArgs e)
    {
        if (_isScanning || _batchLocked || _result is null) return;
        string? scope;
        lock (_watchLock)
        {
            scope = _pendingScope;
            var now = Environment.TickCount64;
            if (scope is null || (now - _lastChange < 900 && now - _firstChange < 3500)) return;
            _pendingScope = null;
        }
        await RefreshCurrentFolderAsync(scope, live: true);
    }
}
