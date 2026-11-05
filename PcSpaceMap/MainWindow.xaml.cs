using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcSpaceMap.Controls;
using PcSpaceMap.Models;
using PcSpaceMap.Services;

namespace PcSpaceMap;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand NewTabCommand = new("New tab", nameof(NewTabCommand), typeof(MainWindow));
    public static readonly RoutedUICommand ScanAllCommand = new("Scan all drives", nameof(ScanAllCommand), typeof(MainWindow));
    public static readonly RoutedUICommand StopAllCommand = new("Stop all scans", nameof(StopAllCommand), typeof(MainWindow));
    public static readonly RoutedUICommand CloseTabCommand = new("Close tab", nameof(CloseTabCommand), typeof(MainWindow));

    private AgentControlServer? _agentServer;
    private bool _isBatchScanning;
    private bool _batchStopped;
    private ScanWorkspaceControl? _batchCurrent;
    private ServiceWindow? _serviceWindow;

    private void ServiceButton_Click(object sender, RoutedEventArgs e) => OpenServiceWindow();

    private void OpenServiceWindow()
    {
        if (_exitRequested) return;
        if (_serviceWindow is { } open) { open.Activate(); return; }
        _serviceWindow = new ServiceWindow { Owner = this };
        try { _serviceWindow.ShowDialog(); }
        finally { _serviceWindow = null; }
    }

    public MainWindow()
    {
        InitializeComponent();
        Title = $"PC Space Map {App.AppVersion}";
        RefreshStartupChoice();
        DesktopStartupBox.SelectionChanged += DesktopStartupBox_SelectionChanged;
        EnsureAtLeastOneTab();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartServiceFooter();
        if (_agentServer is not null) return;
        try
        {
            _agentServer = new AgentControlServer(this);
            await _agentServer.StartAsync();
            AgentApiText.Text = $"LOCAL API · {new Uri(_agentServer.BaseUrl).Port}";
            AgentApiText.ToolTip = $"Loopback-only agent access. Session details: {_agentServer.SessionFilePath}";
        }
        catch (Exception ex)
        {
            AgentApiText.Text = "LOCAL API · UNAVAILABLE";
            AgentApiText.ToolTip = ex.Message;
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        if (!_exitRequested && _trayIcon is not null && _trayOptions.CloseToTray)
        {
            e.Cancel = true;
            // WPF disallows changing Visibility until the Closing event returns.
            _ = Dispatcher.BeginInvoke(HideToTray);
        }
        else if (_exitRequested || AnyWorkspaceScanning())
        {
            e.Cancel = true;
            if (!_exitRequested) _ = Dispatcher.BeginInvoke(async () => await ExitAsync());
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        StopServiceFooter();
        DisposeTray();
        foreach (var tab in DiskTabs.Items.OfType<TabItem>())
            if (tab.Content is ScanWorkspaceControl workspace) workspace.CloseWorkspace();
        if (_agentServer is null) return;

        _agentServer.RemoveSessionFile();
        _agentServer = null;
    }

    private void EnsureAtLeastOneTab()
    {
        if (DiskTabs.Items.Count == 0) AddWorkspaceTab("", activate: true);
    }

    private IEnumerable<ScanWorkspaceControl> GetWorkspaces() =>
        DiskTabs.Items.OfType<TabItem>()
            .Select(x => x.Content)
            .OfType<ScanWorkspaceControl>();

    private ScanWorkspaceControl? CurrentWorkspace => (DiskTabs.SelectedItem as TabItem)?.Content as ScanWorkspaceControl;

    private bool AnyWorkspaceScanning() => GetWorkspaces().Any(x => x.IsScanning);

    private ScanWorkspaceControl CreateWorkspace(string path)
    {
        var workspace = new ScanWorkspaceControl();
        workspace.SetRootPath(path);
        UpdateWorkspaceServiceFooter(workspace);
        workspace.TitleChanged += Workspace_TitleChanged;
        workspace.StopRequested += (_, _) => StopBatch();
        return workspace;
    }

    private TabItem CreateTabItem(ScanWorkspaceControl workspace)
    {
        var tab = new TabItem { Content = workspace, HeaderTemplate = (DataTemplate)FindResource("DiskTabHeader") };
        RefreshTabPresentation(tab, workspace);
        return tab;
    }

    private void Workspace_TitleChanged(object? sender, EventArgs e)
    {
        if (sender is not ScanWorkspaceControl workspace) return;

        var tab = DiskTabs.Items.OfType<TabItem>().FirstOrDefault(x => ReferenceEquals(x.Content, workspace));
        if (tab is not null)
            RefreshTabPresentation(tab, workspace);

        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshTabPresentation(TabItem tab, ScanWorkspaceControl workspace)
    {
        tab.Header = workspace.TabTitle;
        tab.ToolTip = workspace.TabToolTip;
    }

    private ScanWorkspaceControl EnsureWorkspaceTab(string path, bool activate)
    {
        var normalized = NormalizePath(path);
        var tabs = DiskTabs.Items.OfType<TabItem>().ToList();
        var existingTab = tabs
            .FirstOrDefault(x => x.Content is ScanWorkspaceControl workspace &&
                                 string.Equals(NormalizePath(workspace.EffectivePath), normalized, StringComparison.OrdinalIgnoreCase))
            ?? tabs.FirstOrDefault(x => x.Content is ScanWorkspaceControl workspace &&
                                        !workspace.IsScanning && string.IsNullOrWhiteSpace(workspace.EffectivePath));
        if (existingTab?.Content is ScanWorkspaceControl existingWorkspace)
        {
            existingWorkspace.SetRootPath(path);
            RefreshTabPresentation(existingTab, existingWorkspace);
            if (activate) DiskTabs.SelectedItem = existingTab;
            return existingWorkspace;
        }

        return AddWorkspaceTab(path, activate);
    }

    private ScanWorkspaceControl AddWorkspaceTab(string path, bool activate)
    {
        var workspace = CreateWorkspace(path);
        var tab = CreateTabItem(workspace);
        DiskTabs.Items.Add(tab);
        if (activate) DiskTabs.SelectedItem = tab;
        return workspace;
    }

    private void TabCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = DiskTabs is not null && !_isBatchScanning && !_exitRequested &&
            (e.Command == ScanAllCommand ? !AnyWorkspaceScanning()
                : e.Command != CloseTabCommand || CanCloseTab(e.Parameter as TabItem));
        e.Handled = true;
    }

    private void NewTab_Executed(object sender, ExecutedRoutedEventArgs e) => AddWorkspaceTab("", activate: true);

    private void StopAll_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _isBatchScanning;
        e.Handled = true;
    }

    private void StopAll_Executed(object sender, ExecutedRoutedEventArgs e) => StopBatch();

    private void StopBatch()
    {
        if (!_isBatchScanning || _batchStopped) return;
        _batchStopped = true;
        _batchCurrent?.RequestStop();
    }

    private ScanWorkspaceControl[] PrepareDriveTabs(IReadOnlyList<string> drives) =>
        drives.Select(path => EnsureWorkspaceTab(path, activate: false)).ToArray();

    private async void ScanAll_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady)
            .Select(drive => drive.RootDirectory.FullName).ToArray();
        await ScanAllAsync(drives);
    }

    private async Task ScanAllAsync(IReadOnlyList<string> drives)
    {
        if (_isBatchScanning || AnyWorkspaceScanning()) return;

        if (drives.Count == 0)
        {
            CurrentWorkspace?.SetStatus("No ready drives were found.");
            return;
        }

        _isBatchScanning = true;
        _batchStopped = false;
        CommandManager.InvalidateRequerySuggested();
        var completed = 0;
        try
        {
            var queue = PrepareDriveTabs(drives);
            DiskTabs.SelectedItem = DiskTabs.Items.OfType<TabItem>().First(tab => ReferenceEquals(tab.Content, queue[0]));
            for (var index = 0; index < drives.Count && !_exitRequested && !_batchStopped; index++)
            {
                var path = drives[index];
                foreach (var tab in GetWorkspaces())
                    tab.SetBatchCaption($"{path} · {index + 1}/{drives.Count}");
                _batchCurrent = queue[index];
                if (await _batchCurrent.StartScanAsync(path))
                    completed++;
            }

            if (!_exitRequested) CurrentWorkspace?.SetStatus(_batchStopped
                ? $"Batch stopped. Scanned {completed} of {drives.Count} drives."
                : $"Finished scanning {completed} of {drives.Count} drives.");
        }
        finally
        {
            _isBatchScanning = false;
            _batchCurrent = null;
            foreach (var tab in GetWorkspaces())
                tab.SetBatchCaption(null);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool CanCloseTab(TabItem? tab) => !_isBatchScanning && !_exitRequested &&
        tab?.Content is ScanWorkspaceControl { IsScanning: false } && DiskTabs.Items.Contains(tab);

    private void CloseTab_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not TabItem tab || tab.Content is not ScanWorkspaceControl workspace || !CanCloseTab(tab)) return;
        var confirmation = new CloseTabDialog(workspace.TabTitle, workspace.EffectivePath) { Owner = this };
        if (confirmation.ShowDialog() != true || !CanCloseTab(tab)) return;

        var index = DiskTabs.Items.IndexOf(tab);
        var selected = DiskTabs.SelectedItem;
        workspace.CloseWorkspace();
        workspace.TitleChanged -= Workspace_TitleChanged;
        DiskTabs.Items.Remove(tab);
        if (DiskTabs.Items.Count == 0) EnsureAtLeastOneTab();
        else if (ReferenceEquals(tab, selected)) DiskTabs.SelectedIndex = Math.Min(index, DiskTabs.Items.Count - 1);
        else DiskTabs.SelectedItem = selected;
        CommandManager.InvalidateRequerySuggested();
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return value.Trim();
        }
    }

    internal AgentScanResponse BeginAgentScan(string path)
    {
        if (_exitRequested || _isBatchScanning || AnyWorkspaceScanning())
            return new AgentScanResponse(false, "A scan is already running.");
        if (string.IsNullOrWhiteSpace(path) || (!ServicePreferences.IsInstalled() && !Directory.Exists(path)))
            return new AgentScanResponse(false, "The requested folder does not exist.");

        var workspace = EnsureWorkspaceTab(path, activate: true);
        _ = workspace.StartScanAsync(path);
        return new AgentScanResponse(true, $"Scanning {Path.GetFullPath(path)}");
    }

    internal object BuildAgentStatus()
    {
        var snapshot = CurrentWorkspace?.GetSnapshot();
        return new
        {
            app = "PC Space Map",
            version = App.AppVersion,
            isScanning = _isBatchScanning || AnyWorkspaceScanning(),
            status = snapshot?.Status ?? "Open a tab for a disk or folder, then scan it.",
            scanRoot = snapshot?.ScanRoot,
            scannedSize = snapshot?.ScannedSize ?? 0,
            scannedSizeText = snapshot?.ScannedSizeText,
            fileCount = snapshot?.FileCount ?? 0,
            folderCount = snapshot?.FolderCount ?? 0,
            suggestionPotential = snapshot?.SuggestionPotential ?? 0,
            suggestionPotentialText = snapshot?.SuggestionPotentialText,
            viewPath = snapshot?.ViewPath,
            selectedPath = snapshot?.SelectedPath,
            selectedTab = snapshot?.SelectedTab,
            selectedDiskTab = (DiskTabs.SelectedItem as TabItem)?.Header?.ToString(),
            openTabs = DiskTabs.Items.OfType<TabItem>().Select(x => x.Header?.ToString()).ToArray(),
            serviceStatus = _serviceFooter.Text,
            serviceDrives = _serviceFooter.Drives,
            inventoryReady = snapshot?.InventoryReady ?? false,
            readOnly = true
        };
    }

    internal object BuildAgentReport()
    {
        var workspace = CurrentWorkspace;
        if (workspace is null)
            return new { inventoryReady = false, message = _isBatchScanning || AnyWorkspaceScanning() ? "A scan is running." : "No scan tab is selected." };
        return workspace.BuildAgentReport();
    }

    internal object BuildAgentTree(string? path, int depth, int limit)
    {
        var workspace = CurrentWorkspace;
        return workspace is null
            ? new { found = false, inventoryReady = false, message = "No scan tab is selected." }
            : workspace.BuildAgentTree(path, depth, limit);
    }

    internal object BuildAgentLargestFiles(string? under, int limit)
    {
        var workspace = CurrentWorkspace;
        return workspace is null
            ? new { inventoryReady = false, files = Array.Empty<object>() }
            : workspace.BuildAgentLargestFiles(under, limit);
    }

    internal object BuildAgentSuggestions() =>
        CurrentWorkspace?.BuildAgentSuggestions() ?? new { inventoryReady = false, readOnly = true, totalPotential = 0, suggestions = Array.Empty<object>() };

    internal object BuildAgentChanges() => CurrentWorkspace?.BuildAgentChanges() ?? new { inventoryReady = false };
    internal object BuildAgentChildren(string? path, int offset, int limit) => CurrentWorkspace?.BuildAgentChildren(path, offset, limit) ?? new { found = false };
    internal bool BeginAgentUpdate() => !_isBatchScanning && !AnyWorkspaceScanning() && CurrentWorkspace?.BeginUpdateChanges() == true;
    internal bool BeginAgentUpdateFolder(string path) => !_isBatchScanning && !AnyWorkspaceScanning() && CurrentWorkspace?.BeginUpdateFolder(path) == true;
    internal bool BeginAgentOpenSaved(string path) => !_isBatchScanning && !AnyWorkspaceScanning() && EnsureWorkspaceTab(path, activate: true).BeginOpenSaved(path);

    internal object BuildAgentIssues() =>
        CurrentWorkspace?.BuildAgentIssues() ?? new { inventoryReady = false, skippedReparsePoints = 0, issues = Array.Empty<object>() };

    internal AgentNavigationResponse ApplyAgentNavigation(AgentNavigateRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Tab))
        {
            var matchingDiskTab = DiskTabs.Items.OfType<TabItem>()
                .FirstOrDefault(x => string.Equals(x.Header?.ToString(), request.Tab, StringComparison.OrdinalIgnoreCase));
            if (matchingDiskTab is not null)
            {
                DiskTabs.SelectedItem = matchingDiskTab;
                if (string.IsNullOrWhiteSpace(request.Path))
                    return new AgentNavigationResponse(true, "Disk tab selected.");
            }
        }

        var workspace = CurrentWorkspace;
        if (workspace is null)
            return new AgentNavigationResponse(false, "No scan tab is selected.");
        return workspace.ApplyAgentNavigation(request);
    }

    internal byte[] CaptureAgentScreenshot()
    {
        AppSurface.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(AppSurface);
        // Rendering includes the root grid's offset from its window margin.
        var offset = VisualTreeHelper.GetOffset(AppSurface);
        var width = Math.Max(1, (int)Math.Ceiling((AppSurface.ActualWidth + Math.Max(0, offset.X)) * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling((AppSurface.ActualHeight + Math.Max(0, offset.Y)) * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(AppSurface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
