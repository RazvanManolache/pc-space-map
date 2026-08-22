using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcSpaceMap.Controls;
using PcSpaceMap.Models;
using PcSpaceMap.Services;

namespace PcSpaceMap;

public partial class MainWindow : Window
{
    private AgentControlServer? _agentServer;
    private bool _isBatchScanning;

    public MainWindow()
    {
        InitializeComponent();
        LoadDrives();
        EnsureAtLeastOneTab();
        Closing += MainWindow_Closing;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
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
        if (_agentServer is null) return;

        _agentServer.RemoveSessionFile();
        _agentServer = null;
    }

    private void LoadDrives()
    {
        var drives = DriveInfo.GetDrives()
            .Where(x => x.IsReady)
            .Select(x => $"{x.RootDirectory.FullName}   {x.VolumeLabel}   ({SizeFormatter.Format(x.AvailableFreeSpace)} free)")
            .ToList();
        QuickPathBox.ItemsSource = drives;
        QuickPathBox.Text = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "C:\\";
        RefreshShellState();
    }

    private void EnsureAtLeastOneTab()
    {
        if (DiskTabs.Items.Count > 0) return;

        var defaultRoot = ExtractPath(QuickPathBox.Text);
        var workspace = CreateWorkspace(defaultRoot);
        var tab = CreateTabItem(workspace);
        DiskTabs.Items.Add(tab);
        DiskTabs.SelectedItem = tab;
        UpdateShellStatus();
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
        workspace.TitleChanged += Workspace_TitleChanged;
        return workspace;
    }

    private TabItem CreateTabItem(ScanWorkspaceControl workspace)
    {
        var tab = new TabItem { Content = workspace };
        RefreshTabPresentation(tab, workspace);
        return tab;
    }

    private void Workspace_TitleChanged(object? sender, EventArgs e)
    {
        if (sender is not ScanWorkspaceControl workspace) return;

        var tab = DiskTabs.Items.OfType<TabItem>().FirstOrDefault(x => ReferenceEquals(x.Content, workspace));
        if (tab is not null)
            RefreshTabPresentation(tab, workspace);

        RefreshShellState();
        UpdateShellStatus();
    }

    private void RefreshTabPresentation(TabItem tab, ScanWorkspaceControl workspace)
    {
        tab.Header = workspace.TabTitle;
        tab.ToolTip = workspace.TabToolTip;
    }

    private ScanWorkspaceControl EnsureWorkspaceTab(string path, bool activate)
    {
        var normalized = NormalizePath(path);
        var existingTab = DiskTabs.Items.OfType<TabItem>()
            .FirstOrDefault(x => x.Content is ScanWorkspaceControl workspace &&
                                 string.Equals(NormalizePath(workspace.ConfiguredPath), normalized, StringComparison.OrdinalIgnoreCase));
        if (existingTab?.Content is ScanWorkspaceControl existingWorkspace)
        {
            existingWorkspace.SetRootPath(path);
            RefreshTabPresentation(existingTab, existingWorkspace);
            if (activate) DiskTabs.SelectedItem = existingTab;
            return existingWorkspace;
        }

        var workspace = CreateWorkspace(path);
        var tab = CreateTabItem(workspace);
        DiskTabs.Items.Add(tab);
        if (activate) DiskTabs.SelectedItem = tab;
        return workspace;
    }

    private void RefreshShellState()
    {
        var anyScanning = AnyWorkspaceScanning();
        QuickPathBox.IsEnabled = !_isBatchScanning && !anyScanning;
        OpenAndScanButton.IsEnabled = !_isBatchScanning && !anyScanning;
        ScanAllButton.IsEnabled = !_isBatchScanning && !anyScanning;
        OpenTabButton.IsEnabled = !_isBatchScanning;
        CloseTabButton.IsEnabled = !_isBatchScanning && CurrentWorkspace?.IsScanning != true;
    }

    private void UpdateShellStatus()
    {
        if (_isBatchScanning) return;

        var workspace = CurrentWorkspace;
        if (workspace is null)
        {
            ShellStatusText.Text = "Open a tab for a disk or folder, then scan it.";
            return;
        }

        var snapshot = workspace.GetSnapshot();
        if (snapshot.IsScanning || snapshot.InventoryReady)
        {
            ShellStatusText.Text = snapshot.Status;
            return;
        }

        ShellStatusText.Text = $"Tab ready: {workspace.TabToolTip}";
    }

    private async void OpenAndScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchScanning || AnyWorkspaceScanning()) return;

        var requestedPath = ExtractPath(QuickPathBox.Text);
        if (string.IsNullOrWhiteSpace(requestedPath) || !Directory.Exists(requestedPath))
        {
            MessageBox.Show(this, "Choose an existing drive or folder first.", "Folder not found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var workspace = EnsureWorkspaceTab(requestedPath, activate: true);
        RefreshShellState();
        await workspace.StartScanAsync(requestedPath);
        RefreshShellState();
        UpdateShellStatus();
    }

    private void OpenTabButton_Click(object sender, RoutedEventArgs e)
    {
        var requestedPath = ExtractPath(QuickPathBox.Text);
        if (string.IsNullOrWhiteSpace(requestedPath) || !Directory.Exists(requestedPath))
        {
            MessageBox.Show(this, "Choose an existing drive or folder first.", "Folder not found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EnsureWorkspaceTab(requestedPath, activate: true);
        RefreshShellState();
        UpdateShellStatus();
    }

    private async void ScanAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchScanning || AnyWorkspaceScanning()) return;

        var drives = DriveInfo.GetDrives()
            .Where(x => x.IsReady)
            .Select(x => x.RootDirectory.FullName)
            .ToList();
        if (drives.Count == 0)
        {
            ShellStatusText.Text = "No ready drives were found.";
            return;
        }

        _isBatchScanning = true;
        RefreshShellState();
        var completed = 0;
        try
        {
            for (var index = 0; index < drives.Count; index++)
            {
                var path = drives[index];
                ShellStatusText.Text = $"Scanning {path} ({index + 1}/{drives.Count})";
                var workspace = EnsureWorkspaceTab(path, activate: true);
                if (await workspace.StartScanAsync(path))
                    completed++;
            }

            ShellStatusText.Text = $"Finished scanning {completed} of {drives.Count} drives.";
        }
        finally
        {
            _isBatchScanning = false;
            RefreshShellState();
            UpdateShellStatus();
        }
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentWorkspace?.IsScanning == true)
        {
            MessageBox.Show(this, "Stop the active scan before closing this tab.", "Scan running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (DiskTabs.SelectedItem is not TabItem selectedTab) return;

        if (selectedTab.Content is ScanWorkspaceControl workspace)
            workspace.TitleChanged -= Workspace_TitleChanged;

        DiskTabs.Items.Remove(selectedTab);
        EnsureAtLeastOneTab();
        RefreshShellState();
        UpdateShellStatus();
    }

    private void DiskTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshShellState();
        UpdateShellStatus();
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
            return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return value.Trim();
        }
    }

    internal AgentScanResponse BeginAgentScan(string path)
    {
        if (_isBatchScanning || AnyWorkspaceScanning())
            return new AgentScanResponse(false, "A scan is already running.");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
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
            version = "0.3.0",
            isScanning = _isBatchScanning || AnyWorkspaceScanning(),
            status = snapshot?.Status ?? ShellStatusText.Text,
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
        var width = Math.Max(1, (int)Math.Ceiling(AppSurface.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(AppSurface.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(AppSurface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
