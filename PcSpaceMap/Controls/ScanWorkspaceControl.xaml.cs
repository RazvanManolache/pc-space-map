using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PcSpaceMap.Models;
using PcSpaceMap.Services;

namespace PcSpaceMap.Controls;

public partial class ScanWorkspaceControl : UserControl
{
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _treemapPreviewCancellation;
    private ScanResult? _result;
    private ScanNode? _selectedNode;
    private bool _isScanning;
    private bool _batchLocked;
    private bool _serviceRequired;
    private bool _hasScopedRefreshes;
    private int _contentsOffset;
    private long _navigationVersion;
    private ScanSettings _settings = ScanSettings.Load();
    private bool _optionsReady;

    public ScanWorkspaceControl()
    {
        InitializeComponent();
        LoadDrives();
        ScanModeBox.SelectedIndex = _settings.PreferNtfs ? 0 : 1;
        WorkersBox.SelectedIndex = _settings.AutoTuneReaders ? 0 : _settings.Parallelism switch { <= 1 => 1, <= 2 => 2, <= 4 => 3, <= 8 => 4, _ => 5 };
        SetMapDetail(Treemap.Detail);
        _optionsReady = true;
        UpdateScanControls();
    }

    public event EventHandler? TitleChanged;
    internal event EventHandler? StopRequested;

    internal void SetStatus(string status) => StatusText.Text = status;

    internal void SetServiceFooter(string text, string details, bool serviceRequired, IReadOnlyList<ServiceDriveStatus> drives)
    {
        ServiceFooterText.Text = text;
        ServiceFooterText.ToolTip = details;
        ServiceDriveChips.ItemsSource = drives;
        ServiceFooterPanel.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (_serviceRequired != serviceRequired)
        {
            _serviceRequired = serviceRequired;
            ScanButton.Content = serviceRequired ? "Show inventory" : "Scan now";
            ScanButton.ToolTip = serviceRequired
                ? "Open the latest service index immediately. Scan if an index is missing. Use Update changes to refresh an existing index."
                : null;
            if (!_usingService)
                BackendLabel.Text = serviceRequired
                    ? _result is null ? "Windows service" : "Standalone · saved view"
                    : "Standalone";
            UpdateScanControls();
            SyncWatcher();
        }
    }

    internal void SetBatchCaption(string? caption)
    {
        BatchText.Text = caption ?? "";
        BatchText.Visibility = caption is null ? Visibility.Collapsed : Visibility.Visible;
        _batchLocked = caption is not null;
        UpdateScanControls();
    }

    public bool IsScanning => _isScanning;
    public bool InventoryReady => _result is not null;
    public string ConfiguredPath => ExtractPath(RootPathBox.Text);
    public string EffectivePath => _result?.Root.Path ?? NormalizePath(ConfiguredPath);
    public string TabTitle => BuildTabTitle();
    public string TabToolTip => string.IsNullOrWhiteSpace(EffectivePath) ? "Choose a drive or folder to scan." : EffectivePath;
    private bool PickerMatchesInventory => _result is not null && string.Equals(
        Path.TrimEndingDirectorySeparator(NormalizePath(ConfiguredPath)),
        Path.TrimEndingDirectorySeparator(_result.Root.Path), StringComparison.OrdinalIgnoreCase);

    public void SetRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RootPathBox.Text = NormalizePath(path);
        PickerChanged();
    }

    private void RootPathBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // ComboBox updates Text after SelectionChanged for a picked drive.
        _ = Dispatcher.BeginInvoke(new Action(PickerChanged));
    }

    private void PickerChanged()
    {
        UpdateScanControls();
        if (!_isScanning && _result is not null)
            StatusText.Text = PickerMatchesInventory ? $"Showing {_result.Root.Path}."
                : $"Showing {_result.Root.Path}. Scan or open saved results for {ConfiguredPath} to switch.";
        RaiseTitleChanged();
    }

    public async Task<bool> StartScanAsync(string? requestedPath = null)
    {
        if (_workspaceClosed || _isScanning) return false;

        var requested = requestedPath ?? ConfiguredPath;
        if (string.IsNullOrWhiteSpace(requested))
        {
            MessageBox.Show(Window.GetWindow(this), "Choose an existing drive or folder first.", "Folder not found", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        _isScanning = true;
        _scanCancellation = new CancellationTokenSource();
        SetScanningState(true);
        RootPathBox.Text = NormalizePath(requested);
        RaiseTitleChanged();

        var progress = new Progress<ScanProgress>(p =>
        {
            if (_result is null)
            {
                TotalSizeText.Text = SizeFormatter.Format(p.BytesFound);
                FileCountText.Text = p.FileCount.ToString("N0");
                FolderCountText.Text = p.DirectoryCount.ToString("N0");
            }
            UpdateScanProgress(p);
            RaiseTitleChanged();
        });

        var completed = false;
        try
        {
            var remote = await ServiceInventoryClient.ScanAsync(requested, "scan", progress, _scanCancellation.Token, openPublished: true);
            _result = remote ?? await _settings.Scanner().ScanAsync(requested, progress, _scanCancellation.Token);
            SetBackend(remote is not null);
            _hasScopedRefreshes = false;
            ShowResult(_result, remote is null ? null : $"Service inventory opened · indexed {remote.CompletedUtc.ToLocalTime():g}");
            completed = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _result is null ? "Scan stopped. Partial results were discarded."
                : $"Scan stopped. Still showing {_result.Root.Path}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = _result is null ? "Scan could not be completed."
                : $"Scan failed. Still showing {_result.Root.Path}.";
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!completed && _result is null)
                TotalSizeText.Text = FileCountText.Text = FolderCountText.Text = "—";
            _isScanning = false;
            SetScanningState(false);
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            RaiseTitleChanged();
        }

        return completed;
    }

    public async Task<bool> RefreshCurrentFolderAsync(string? requestedPath = null, bool live = false)
    {
        if (_isScanning || _result is null || (live && (_usingService || _serviceRequired)) || (!live && !PickerMatchesInventory)) return false;

        var targetPath = requestedPath ?? GetRefreshTargetNode()?.Path;
        if (targetPath is null)
        {
            MessageBox.Show(Window.GetWindow(this), "Select or zoom into a folder first.", "No folder selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var selectedPathBefore = _selectedNode?.Path;
        var viewPathBefore = Treemap.Root?.Path;

        _isScanning = true;
        _scanCancellation = new CancellationTokenSource();
        SetScanningState(true);
        StatusText.Text = $"Updating {targetPath}";
        ElapsedText.Text = "";
        RaiseTitleChanged();

        var progress = new Progress<ScanProgress>(p =>
        {
            UpdateScanProgress(p);
            RaiseTitleChanged();
        });

        var completed = false;
        try
        {
            var journal = live && string.Equals(_result.Root.Path, targetPath, StringComparison.OrdinalIgnoreCase);
            var remote = await ServiceInventoryClient.ScanAsync(_result.Root.Path, "update", progress, _scanCancellation.Token,
                folderToRefresh: targetPath);
            var updated = remote ?? (_usingService
                ? await _settings.Scanner().ScanAsync(_result.Root.Path, progress, _scanCancellation.Token)
                : await _settings.Scanner().RefreshAsync(_result, journal ? null : targetPath, progress, _scanCancellation.Token));
            if (live)
            {
                selectedPathBefore = _selectedNode?.Path;
                viewPathBefore = Treemap.Root?.Path;
            }
            _result = updated;
            SetBackend(remote is not null);
            _hasScopedRefreshes = !string.Equals(_result.Root.Path, targetPath, StringComparison.OrdinalIgnoreCase);
            ShowResult(_result, live ? $"Live update complete: {targetPath}" : $"Folder updated: {targetPath}",
                $"Updated in {FormatDuration(_result.Duration)}", preserveView: true);
            RestoreNavigation(viewPathBefore, selectedPathBefore);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Folder update stopped. Existing inventory was kept.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Folder update could not be completed.";
            if (live) StatusText.Text = "Live update failed: " + ex.Message;
            else MessageBox.Show(Window.GetWindow(this), ex.Message, "Folder update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isScanning = false;
            SetScanningState(false);
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            RaiseTitleChanged();
        }

        return completed;
    }

    public WorkspaceSnapshot GetSnapshot()
    {
        var viewRoot = Treemap.Root;
        var suggestionPotential = _result?.Suggestions.Sum(x => x.Size) ?? 0;
        return new WorkspaceSnapshot(
            InventoryReady,
            _isScanning,
            StatusText.Text,
            _result?.Root.Path,
            _result?.Root.Size ?? 0,
            _result?.Root.SizeText,
            _result?.FileCount ?? 0,
            _result?.DirectoryCount ?? 0,
            suggestionPotential,
            _result is null ? null : SizeFormatter.Format(suggestionPotential),
            viewRoot?.Path,
            _selectedNode?.Path,
            (MainTabs.SelectedItem as TabItem)?.Header?.ToString() ?? "Space map",
            true);
    }

    internal object BuildAgentReport()
    {
        if (_result is null)
            return new { inventoryReady = false, message = _isScanning ? "A scan is running." : "No inventory has been scanned yet." };

        return new
        {
            inventoryReady = true,
            root = ToAgentNode(_result.Root, 0, 0),
            durationSeconds = Math.Round(_result.Duration.TotalSeconds, 2),
            snapshotId = _result.SnapshotId,
            completedUtc = _result.CompletedUtc,
            scanMethod = _result.ScanMethod,
            timings = _result.Timings,
            work = _result.Work,
            backend = _usingService ? "Windows service" : "Standalone",
            allocatedBytes = _result.Root.AllocatedSize,
            uniqueAllocatedBytes = _result.Root.UniqueAllocatedSize,
            changes = _result.Changes,
            fileCount = _result.FileCount,
            folderCount = _result.DirectoryCount,
            skippedReparsePoints = _result.SkippedReparsePoints,
            issueCount = _result.Issues.Count,
            topLevel = _result.Root.Children.Where(x => x.Size > 0).Take(50).Select(x => ToAgentNode(x, 0, 0)),
            largestFiles = _result.LargestFiles.Take(100).Select(x => ToAgentNode(x, 0, 0)),
            suggestions = _result.Suggestions.Select(ToAgentSuggestion),
            safety = "Inventory and assistant controls are read-only. Windows context-menu commands can modify files."
        };
    }

    internal object BuildAgentTree(string? path, int depth, int limit)
    {
        if (_result is null)
            return new { found = false, inventoryReady = false, message = "No inventory has been scanned yet." };

        var node = string.IsNullOrWhiteSpace(path) ? _result.Root : FindNode(path);
        if (node is null)
            return new { found = false, inventoryReady = true, message = "The path was not found in the current inventory.", requestedPath = path };

        return new
        {
            found = true,
            inventoryReady = true,
            node = ToAgentNode(node, depth, limit)
        };
    }

    internal object BuildAgentLargestFiles(string? under, int limit)
    {
        if (_result is null)
            return new { inventoryReady = false, files = Array.Empty<object>() };

        var basePath = string.IsNullOrWhiteSpace(under) ? _result.Root.Path : Path.GetFullPath(under);
        if (!IsSameOrBelow(basePath, _result.Root.Path))
            return new { inventoryReady = true, under = basePath, limit, files = Array.Empty<object>() };
        var files = (_result.Inventory?.Largest(_result.SnapshotId, basePath, limit) ?? _result.LargestFiles)
            .Where(x => IsSameOrBelow(x.Path, basePath))
            .Take(limit)
            .Select(x => ToAgentNode(x, 0, 0));
        return new { inventoryReady = true, under = basePath, limit, files };
    }

    internal object BuildAgentSuggestions() => new
    {
        inventoryReady = _result is not null,
        readOnly = true,
        totalPotential = _result?.Suggestions.Sum(x => x.Size) ?? 0,
        suggestions = _result?.Suggestions.Select(ToAgentSuggestion) ?? []
    };

    internal object BuildAgentChanges() => new
    {
        inventoryReady = _result is not null,
        snapshotId = _result?.SnapshotId,
        completedUtc = _result?.CompletedUtc,
        changes = _result?.Changes ?? []
    };

    internal object BuildAgentChildren(string? path, int offset, int limit)
    {
        var node = string.IsNullOrWhiteSpace(path) ? Treemap.Root : FindNode(path);
        if (_result?.Inventory is null || node?.IsDirectory != true) return new { found = false };
        return new { found = true, path = node.Path, offset, limit, total = node.ChildCount,
            entries = _result.Inventory.Children(_result.SnapshotId, node, offset, limit).Select(x => ToAgentNode(x, 0, 0)).ToArray() };
    }

    internal bool BeginUpdateChanges()
    {
        if (_isScanning || !PickerMatchesInventory) return false;
        UpdateChangesButton_Click(this, new RoutedEventArgs());
        return true;
    }
    internal bool BeginUpdateFolder(string path)
    {
        if (_isScanning || _result is null || !PickerMatchesInventory || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            path = InventoryStore.Normalize(path);
            if (!IsSameOrBelow(path, _result.Root.Path)) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        _ = RefreshCurrentFolderAsync(path);
        return true;
    }

    internal bool BeginOpenSaved(string path)
    {
        if (_isScanning) return false;
        SetRootPath(path);
        SavedButton_Click(this, new RoutedEventArgs());
        return true;
    }

    internal object BuildAgentIssues() => new
    {
        inventoryReady = _result is not null,
        skippedReparsePoints = _result?.SkippedReparsePoints ?? 0,
        issues = _result?.Issues.Select(x => new { x.Path, x.Message }) ?? []
    };

    internal AgentNavigationResponse ApplyAgentNavigation(AgentNavigateRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Tab))
        {
            var matchingTab = MainTabs.Items.OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(x.Header?.ToString(), request.Tab, StringComparison.OrdinalIgnoreCase));
            if (matchingTab is null)
                return new AgentNavigationResponse(false, $"Unknown tab: {request.Tab}");
            MainTabs.SelectedItem = matchingTab;
        }

        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            var node = FindNode(request.Path);
            if (node is null)
                return new AgentNavigationResponse(false, "The path was not found in the current inventory.");
            SelectNode(node);
            if (!request.SelectOnly && node.IsDirectory)
                ZoomTo(node);
        }

        return new AgentNavigationResponse(true, "View updated without desktop input.");
    }

    private void LoadDrives()
    {
        var drives = DriveInfo.GetDrives()
            .Where(x => x.IsReady)
            .Select(x => $"{x.RootDirectory.FullName}   {x.VolumeLabel}   ({SizeFormatter.Format(x.AvailableFreeSpace)} free)")
            .ToList();
        RootPathBox.ItemsSource = drives;
    }

    private void ShowResult(ScanResult result, string? statusOverride = null, string? elapsedOverride = null, bool preserveView = false)
    {
        TotalSizeText.Text = result.Root.SizeText;
        FileCountText.Text = result.FileCount.ToString("N0");
        FolderCountText.Text = result.DirectoryCount.ToString("N0");
        CleanupSizeText.Text = SizeFormatter.Format(result.Suggestions.Sum(x => x.Size));
        ElapsedText.Text = elapsedOverride ?? $"Completed in {FormatDuration(result.Duration)}";
        StatusText.Text = statusOverride ?? $"Inventory complete: {result.Root.Path}";

        if (!preserveView || _mapFilter is null) LargestFilesGrid.ItemsSource = result.LargestFiles;
        RefreshStoredLists(result);
        if (!preserveView)
        {
            _searchCancellation?.Cancel();
            if (ReferenceEquals(MainTabs.SelectedItem, SearchTab)) _ = RunSearchAsync(0);
        }
        ChangesSummaryText.Text = result.Changes.Count == 0 ? "No changes, or no previous inventory for comparison."
            : $"Largest {result.Changes.Count:N0} changes since the preceding saved inventory. Folder rows include their descendants; do not add rows together.";
        var readerLabel = result.Work is { ReaderCount: > 0 } work ? $" | {work.ReaderCount} readers" : "";
        MethodText.Text = $"{result.ScanMethod}{readerLabel} | {result.CompletedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        IssueSummaryText.Text = BuildIssueSummary(result);
        ExportButton.IsEnabled = true;
        if (!preserveView)
        {
            if (!ReferenceEquals(MainTabs.SelectedItem, SearchTab)) MainTabs.SelectedIndex = 0;
            ResetMapHistory();
            ZoomTo(result.Root, remember: false);
            SelectNode(result.Root);
        }
        SyncWatcher();
        RaiseTitleChanged();
    }

    private void SetScanningState(bool scanning)
    {
        UpdateScanControls();
        CancelButton.IsEnabled = scanning && _scanCancellation is not null;
        ScanProgressPanel.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = true;
        ScanPercentText.Text = "";
        if (scanning) StatusText.Text = "Starting scan…";
        RaiseTitleChanged();
    }

    private void UpdateScanProgress(ScanProgress progress)
    {
        if (!_isScanning) return;
        var percent = progress.PhasePercent;
        ScanProgressBar.IsIndeterminate = !percent.HasValue;
        ScanProgressBar.Value = percent ?? 0;
        ScanPercentText.Text = percent is int value ? $"{value}%" : "";
        ScanProgressPanel.ToolTip = percent.HasValue ? $"{progress.Phase} · {percent}% of this phase" : progress.Phase;
        StatusText.Text = progress.CurrentPath.Length > 0 && progress.CurrentPath != progress.Phase
            ? $"{progress.Phase} · {progress.CurrentPath}" : progress.Phase;
    }

    private void UpdateScanControls()
    {
        var available = !_workspaceClosed && !_isScanning && !_batchLocked;
        var hasPath = !string.IsNullOrWhiteSpace(ConfiguredPath);
        ScanButton.IsEnabled = available && hasPath;
        SavedButton.IsEnabled = available && hasPath;
        BrowseButton.IsEnabled = available;
        RootPathBox.IsEnabled = available;
        var refreshTarget = PickerMatchesInventory ? GetRefreshTargetNode() : null;
        RefreshFolderButton.IsEnabled = available && refreshTarget is not null;
        RefreshSelectedButton.IsEnabled = RefreshFolderButton.IsEnabled;
        RefreshSelectedButton.ToolTip = RefreshFolderButton.ToolTip = refreshTarget is not null ? $"Refresh {refreshTarget.Path}"
            : null;
        UpdateChangesButton.IsEnabled = available && PickerMatchesInventory;
        ScanModeBox.IsEnabled = available && !_serviceRequired;
        WorkersBox.IsEnabled = available && !_serviceRequired;
        WatchBox.IsEnabled = available && !_serviceRequired && !_usingService;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await StartScanAsync();
    private async void RefreshFolderButton_Click(object sender, RoutedEventArgs e) => await RefreshCurrentFolderAsync();

    private void ScanOptions_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_optionsReady) return;
        _settings.PreferNtfs = ScanModeBox.SelectedIndex == 0;
        var readers = ((ComboBoxItem)WorkersBox.SelectedItem).Content.ToString()!;
        _settings.AutoTuneReaders = readers == "Auto";
        if (!_settings.AutoTuneReaders) _settings.Parallelism = int.Parse(readers);
        try { _settings.Save(); } catch (Exception ex) { StatusText.Text = "Could not save scan options: " + ex.Message; }
    }

    private async void SavedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning) return;
        _isScanning = true;
        SetScanningState(true);
        try
        {
            var (usesService, saved) = await ServiceInventoryClient.OpenSavedAsync(ConfiguredPath);
            var result = usesService ? saved : await new DiskScanner().LoadSavedAsync(ConfiguredPath);
            if (result is null) { StatusText.Text = "No saved inventory for this path."; return; }
            _result = result;
            SetBackend(usesService);
            ShowResult(result, "Saved inventory opened. " + result.CompletedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        }
        catch (Exception ex) { StatusText.Text = "Could not open inventory: " + ex.Message; }
        finally { _isScanning = false; SetScanningState(false); RaiseTitleChanged(); }
    }

    private async void UpdateChangesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _result is null || !PickerMatchesInventory) return;
        var view = Treemap.Root?.Path;
        _isScanning = true;
        _scanCancellation = new();
        SetScanningState(true);
        try
        {
            var progress = new Progress<ScanProgress>(UpdateScanProgress);
            var remote = await ServiceInventoryClient.ScanAsync(_result.Root.Path, "update", progress, _scanCancellation.Token);
            _result = remote ?? (_usingService
                ? await _settings.Scanner().ScanAsync(_result.Root.Path, progress, _scanCancellation.Token)
                : await _settings.Scanner().RefreshAsync(_result, null, progress, _scanCancellation.Token));
            SetBackend(remote is not null);
            _hasScopedRefreshes = false;
            ShowResult(_result, preserveView: true);
            RestoreNavigation(view, null);
        }
        catch (OperationCanceledException) { StatusText.Text = "Update stopped. Previous inventory retained."; }
        catch (Exception ex) { StatusText.Text = "Update failed: " + ex.Message; }
        finally { _isScanning = false; _scanCancellation.Dispose(); _scanCancellation = null; SetScanningState(false); RaiseTitleChanged(); }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder or drive to scan",
            InitialDirectory = Directory.Exists(ConfiguredPath) ? ConfiguredPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            SetRootPath(dialog.FolderName);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke(this, EventArgs.Empty);
        RequestStop();
    }

    internal void RequestStop()
    {
        if (_scanCancellation is not { IsCancellationRequested: false } cancellation) return;
        CancelButton.IsEnabled = false;
        StatusText.Text = "Stopping…";
        cancellation.Cancel();
        RaiseTitleChanged();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export complete file inventory",
            Filter = "CSV spreadsheet (*.csv)|*.csv",
            FileName = $"PC-Space-Inventory-{DateTime.Now:yyyy-MM-dd-HHmm}.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        ExportButton.IsEnabled = false;
        StatusText.Text = "Exporting inventory…";
        try
        {
            await CsvExporter.ExportAsync(_result, dialog.FileName);
            StatusText.Text = $"Inventory exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Inventory export failed.";
        }
        finally
        {
            ExportButton.IsEnabled = true;
            RaiseTitleChanged();
        }
    }

    private void Treemap_NodeSelected(object sender, ScanNode node) => SelectNode(ResolveMapNode(node));
    private void Treemap_ZoomRequested(object sender, ScanNode node)
    {
        if (ResolveMapNode(node) is { } folder) ZoomTo(folder);
    }

    // Filtered tiles carry matching sizes. Navigation and folder operations need the inventory node.
    private ScanNode? ResolveMapNode(ScanNode node) =>
        node.IsDirectory && ReferenceEquals(Treemap.FilteredFolder(node.Path), node) ? FindNode(node.Path) : node;

    private void SelectNode(ScanNode? node)
    {
        _selectedNode = node;
        Treemap.SelectedPath = node?.Path;
        SelectedNameText.Text = node?.Name ?? "Nothing selected";
        UpdateSelectedSummary(node);
        SelectedPathText.Text = node?.Path ?? "—";
        OpenSelectedButton.IsEnabled = node is not null;
        ZoomSelectedButton.IsEnabled = node?.IsDirectory == true;
        UpdateScanControls();
    }

    private void UpdateSelectedSummary(ScanNode? node)
    {
        var matching = node is { IsDirectory: true } ? Treemap.FilteredFolder(node.Path) : null;
        SelectedSizeText.Text = matching?.SizeText ?? node?.SizeText ?? "—";
        SelectedContentsText.Text = matching is null ? BuildSelectedSummary(node)
            : $"{matching.FileCount:N0} matching {(matching.FileCount == 1 ? "file" : "files")} shown";
    }

    private async void ZoomTo(ScanNode node, bool remember = true, int historyDirection = 0, ScanNode? selection = null,
        bool keepMap = false)
    {
        if (!node.IsDirectory || (_result is not null && !IsSameOrBelow(node.Path, _result.Root.Path))) return;
        _treemapPreviewCancellation?.Cancel();
        var inventory = _result;
        var version = ++_navigationVersion;
        if (!node.ChildrenLoaded)
        {
            try { await Task.Run(() => _ = node.Children); }
            catch (Exception ex) { StatusText.Text = "Could not load folder: " + ex.Message; return; }
            if (_result != inventory || version != _navigationVersion) return;
        }
        var previous = Treemap.Root;
        keepMap = keepMap && string.Equals(previous?.Path, node.Path, StringComparison.OrdinalIgnoreCase);
        var preview = keepMap ? await ReadTreemapPreviewAsync(node, inventory, version) : null;
        if (_workspaceClosed || _result != inventory || version != _navigationVersion) return;
        CommitMapNavigation(previous?.Path, node.Path, remember, historyDirection);
        Treemap.Root = node;
        if (preview is not null) Treemap.SetPreview(preview);
        if (previous is not null && !ReferenceEquals(previous, node)) previous.ReleaseChildren();
        SelectNode(selection ?? node);
        RefreshFreeTile();
        BuildBreadcrumbs(node);
        _contentsOffset = 0;
        await Task.WhenAll(ShowContentsAsync(node, inventory),
            keepMap ? Task.CompletedTask : LoadTreemapPreviewAsync(node, inventory, version), RefreshFilterAsync(keepMap));
    }

    private async Task LoadTreemapPreviewAsync(ScanNode node, ScanResult? inventory, long version)
    {
        var preview = await ReadTreemapPreviewAsync(node, inventory, version);
        if (preview is not null && _result == inventory && version == _navigationVersion) Treemap.SetPreview(preview);
    }

    private async Task<IReadOnlyDictionary<ScanNode, IReadOnlyList<ScanNode>>?> ReadTreemapPreviewAsync(
        ScanNode node, ScanResult? inventory, long version)
    {
        if (inventory?.Inventory is not { } store || node.Size <= 0) return null;
        using var cancellation = new CancellationTokenSource();
        _treemapPreviewCancellation = cancellation;
        try
        {
            var children = node.Children.ToArray();
            return await Task.Run(() =>
            {
                var result = new Dictionary<ScanNode, IReadOnlyList<ScanNode>>();
                var pending = new PriorityQueue<(ScanNode Node, int Depth), long>();
                void Enqueue(IEnumerable<ScanNode> candidates, int depth)
                {
                    if (depth >= TreemapControl.MaxDepth) return;
                    foreach (var candidate in candidates)
                        if (candidate.IsDirectory && candidate.Size > 0 && candidate.Size >= node.Size / 3500)
                            pending.Enqueue((candidate, depth), -candidate.Size);
                }
                Enqueue(children, 0);
                while (result.Count < 96 && pending.TryDequeue(out var item, out _))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var entries = store.Children(inventory.SnapshotId, item.Node, limit: 64);
                    result.Add(item.Node, entries);
                    Enqueue(entries, item.Depth + 1);
                }
                return result;
            }, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_result == inventory && version == _navigationVersion)
                StatusText.Text = "Could not load treemap detail: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_treemapPreviewCancellation, cancellation)) _treemapPreviewCancellation = null;
        }
        return null;
    }

    private async Task ShowContentsAsync(ScanNode node, ScanResult? inventory)
    {
        if (inventory?.Inventory is not { } store) return;
        const int pageSize = 200;
        var offset = _contentsOffset;
        var filter = _listNameFilter;
        _contentsCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _contentsCancellation = cancellation;
        var token = cancellation.Token;
        ContentsPreviousButton.IsEnabled = ContentsNextButton.IsEnabled = false;
        try
        {
            var page = await Task.Run(() => store.Children(inventory.SnapshotId, node, offset, pageSize + 1, filter, token), token);
            if (token.IsCancellationRequested || _result != inventory || Treemap.Root != node || _contentsOffset != offset) return;
            var rows = page.Take(pageSize).ToArray();
            ContentsGrid.ItemsSource = rows;
            ContentsPageText.Text = rows.Length == 0 ? (filter is null ? "No entries" : "No matching entries")
                : $"{offset + 1:N0}–{offset + rows.Length:N0}" + (filter is null ? $" of {node.ChildCount:N0}" : " matching names");
            ContentsPreviousButton.IsEnabled = offset > 0;
            ContentsNextButton.IsEnabled = page.Count > pageSize;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ContentsPageText.Text = "Could not load folder: " + ex.Message; }
        finally { if (ReferenceEquals(_contentsCancellation, cancellation)) _contentsCancellation = null; }
    }

    private async void ContentsPage_Click(object sender, RoutedEventArgs e)
    {
        if (Treemap.Root is not { } node) return;
        _contentsOffset = Math.Max(0, _contentsOffset + (ReferenceEquals(sender, ContentsNextButton) ? 200 : -200));
        await ShowContentsAsync(node, _result);
    }

    private void InventoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid) OpenInventoryRow(grid);
    }

    private void InventoryGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not DataGrid { SelectedItem: not null } grid) return;
        OpenInventoryRow(grid);
        e.Handled = true;
    }

    private void OpenInventoryRow(DataGrid grid)
    {
        if (grid.SelectedItem is ScanNode node)
        {
            if (node.IsDirectory)
            {
                if (ReferenceEquals(grid, SearchResultsGrid)) MainTabs.SelectedIndex = 0;
                ZoomTo(node);
            }
            else OpenInExplorer(node.Path, selectFile: true);
        }
        else if (grid.SelectedItem is CleanupSuggestion suggestion)
            OpenInExplorer(suggestion.SamplePath, selectFile: true);
    }

    private void BuildBreadcrumbs(ScanNode current)
    {
        BreadcrumbPanel.Children.Clear();
        var chain = new Stack<ScanNode>();
        for (var node = current; node is not null; node = node.Parent)
        {
            chain.Push(node);
            if (string.Equals(node.Path, _result?.Root.Path, StringComparison.OrdinalIgnoreCase)) break;
        }

        while (chain.Count > 0)
        {
            var node = chain.Pop();
            var button = new Button
            {
                Content = node.Name,
                Tag = node,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(1, 0, 1, 0),
                FontSize = 12
            };
            button.Click += (_, _) => ZoomTo((ScanNode)button.Tag);
            BreadcrumbPanel.Children.Add(button);
            if (chain.Count > 0)
                BreadcrumbPanel.Children.Add(new TextBlock { Text = "›", Foreground = FindResource("SecondaryText") as System.Windows.Media.Brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
        }
        BreadcrumbScroll.ScrollToRightEnd();
    }

    private void OpenSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is not null) OpenInExplorer(_selectedNode.Path, !_selectedNode.IsDirectory);
    }

    private void ZoomSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode?.IsDirectory == true) ZoomTo(_selectedNode);
    }

    private static void OpenInExplorer(string path, bool selectFile)
    {
        try
        {
            var arguments = selectFile && File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch
        {
            // The file may have moved since the scan. Explorer itself will show the current state.
        }
    }

    private static string ExtractPath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' && trimmed[2] == '\\' && char.IsWhiteSpace(trimmed[3]))
            return trimmed[..3];
        return trimmed;
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
        : $"{duration.TotalSeconds:N1}s";

    private string BuildTabTitle()
    {
        var basePath = EffectivePath;
        var title = string.IsNullOrWhiteSpace(basePath) ? "New tab" : ToShortTabLabel(basePath);
        return _isScanning ? $"{title} • scanning" : title;
    }

    private static string ToShortTabLabel(string path)
    {
        var normalized = NormalizePath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized)) return "New scan";

        if (normalized.Length == 2 && normalized[1] == ':')
            return normalized;

        var root = Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
            return root;

        var leaf = Path.GetFileName(normalized);
        if (!string.IsNullOrWhiteSpace(leaf)) return leaf;
        return normalized;
    }

    private void RaiseTitleChanged() => TitleChanged?.Invoke(this, EventArgs.Empty);

    private string BuildSelectedSummary(ScanNode? node)
    {
        if (node is null)
            return "Click a rectangle to inspect it. Double-click folders to zoom.";

        if (node.IsDirectory && TryGetDriveUsage(node.Path, out var usage))
            return $"{node.FileCount:N0} files · {node.DirectoryCount:N0} folders · {usage}";

        return node.ContentsText + $" · {node.AllocatedText} allocated" +
            (node.UniqueAllocatedSize.HasValue ? $" · {SizeFormatter.Format(node.UniqueAllocatedSize.Value)} excluding repeated hard links" : "");
    }

    private string BuildIssueSummary(ScanResult result)
    {
        var baseSummary = result.Issues.Count == 0
            ? $"No explicit file-system errors. {result.SkippedReparsePoints:N0} links/junctions were skipped to avoid loops. Windows may silently omit protected locations."
            : $"{result.Issues.Count:N0} paths could not be fully read. {result.SkippedReparsePoints:N0} links/junctions were skipped to avoid loops.";

        if (!_hasScopedRefreshes)
            return baseSummary;

        return $"{baseSummary} After scoped folder updates, the link/junction count may be stale outside the refreshed folder. Run a full scan to refresh it globally.";
    }

    private ScanNode? GetRefreshTargetNode()
    {
        if (_result is null)
            return null;

        if (_selectedNode?.IsDirectory == true)
            return _selectedNode;

        if (_selectedNode is { IsDirectory: false } file)
            return file.Parent ?? (Path.GetDirectoryName(file.Path) is { } parent ? FindNode(parent) : null);

        if (Treemap.Root?.IsDirectory == true)
            return Treemap.Root;

        return _result.Root.IsDirectory ? _result.Root : null;
    }


    private void RestoreNavigation(string? viewPathBefore, string? selectedPathBefore)
    {
        if (_result is null) return;
        var viewNode = !string.IsNullOrWhiteSpace(viewPathBefore) ? FindNode(viewPathBefore) : null;
        var selectedNode = !string.IsNullOrWhiteSpace(selectedPathBefore) ? FindNode(selectedPathBefore) : null;
        ZoomTo(viewNode?.IsDirectory == true ? viewNode : _result.Root, remember: false, selection: selectedNode, keepMap: true);
    }


    private static bool TryGetDriveUsage(string path, out string usage)
    {
        usage = "";
        try
        {
            if (GetReadyDriveRoot(path) is not { } drive) return false;
            usage = $"{SizeFormatter.Format(drive.AvailableFreeSpace)} free of {SizeFormatter.Format(drive.TotalSize)}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private ScanNode? FindNode(string path)
    {
        if (_result is null || !IsSameOrBelow(path, _result.Root.Path)) return null;
        if (_result.Inventory is not null) return _result.Inventory.Find(_result.SnapshotId, path);
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPath = _result.Root.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(target, rootPath, StringComparison.OrdinalIgnoreCase)) return _result.Root;
        if (!IsSameOrBelow(target, rootPath)) return null;

        var relative = Path.GetRelativePath(rootPath, target);
        if (relative.StartsWith("..", StringComparison.Ordinal)) return null;
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = _result.Root;
        foreach (var part in parts)
        {
            current = current.Children.FirstOrDefault(x => string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase));
            if (current is null) return null;
        }
        return current;
    }

    private object ToAgentNode(ScanNode node, int depth, int limit)
    {
        var children = depth > 0 && limit > 0 ?
            (_result?.Inventory?.Children(_result.SnapshotId, node, 0, limit) ?? node.Children).Take(limit).ToList() : [];
        var includedSize = children.Sum(x => x.Size);
        return new
        {
            node.Name,
            node.Path,
            kind = node.IsDirectory ? "folder" : "file",
            node.Size,
            node.AllocatedSize,
            node.UniqueAllocatedSize,
            node.SizeText,
            node.FileCount,
            node.DirectoryCount,
            node.ModifiedText,
            childCount = node.IsDirectory ? node.ChildCount : 0,
            returnedChildCount = children.Count,
            omittedChildCount = Math.Max(0, (node.IsDirectory ? node.ChildCount : 0) - children.Count),
            omittedSize = Math.Max(0, node.Size - includedSize),
            children = depth > 0 ? children.Select(x => ToAgentNode(x, depth - 1, limit)) : []
        };
    }

    private static object ToAgentSuggestion(CleanupSuggestion suggestion) => new
    {
        suggestion.Category,
        confidence = suggestion.ConfidenceText,
        suggestion.Size,
        suggestion.SizeText,
        suggestion.FileCount,
        suggestion.Reason,
        suggestion.SamplePath
    };

    private static bool IsSameOrBelow(string candidate, string basePath)
    {
        var normalizedBase = InventoryStore.Normalize(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = InventoryStore.Normalize(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedCandidate, normalizedBase, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record WorkspaceSnapshot(
    bool InventoryReady,
    bool IsScanning,
    string Status,
    string? ScanRoot,
    long ScannedSize,
    string? ScannedSizeText,
    long FileCount,
    long FolderCount,
    long SuggestionPotential,
    string? SuggestionPotentialText,
    string? ViewPath,
    string? SelectedPath,
    string SelectedTab,
    bool ReadOnly);
