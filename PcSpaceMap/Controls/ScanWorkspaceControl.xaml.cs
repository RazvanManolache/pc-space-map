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
    private ScanResult? _result;
    private ScanNode? _selectedNode;
    private bool _isScanning;
    private bool _hasScopedRefreshes;

    public ScanWorkspaceControl()
    {
        InitializeComponent();
        LoadDrives();
    }

    public event EventHandler? TitleChanged;

    public bool IsScanning => _isScanning;
    public bool InventoryReady => _result is not null;
    public string ConfiguredPath => ExtractPath(RootPathBox.Text);
    public string EffectivePath => _result?.Root.Path ?? NormalizePath(ConfiguredPath);
    public string TabTitle => BuildTabTitle();
    public string TabToolTip => string.IsNullOrWhiteSpace(EffectivePath) ? "Choose a drive or folder to scan." : EffectivePath;

    public void SetRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RootPathBox.Text = NormalizePath(path);
        RaiseTitleChanged();
    }

    public async Task<bool> StartScanAsync(string? requestedPath = null)
    {
        if (_isScanning) return false;

        var requested = requestedPath ?? ConfiguredPath;
        if (string.IsNullOrWhiteSpace(requested) || !Directory.Exists(requested))
        {
            MessageBox.Show(Window.GetWindow(this), "Choose an existing drive or folder first.", "Folder not found", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        _isScanning = true;
        _scanCancellation = new CancellationTokenSource();
        SetScanningState(true);
        ClearResults();
        RootPathBox.Text = NormalizePath(requested);
        RaiseTitleChanged();

        var progress = new Progress<ScanProgress>(p =>
        {
            TotalSizeText.Text = SizeFormatter.Format(p.BytesFound);
            FileCountText.Text = p.FileCount.ToString("N0");
            FolderCountText.Text = p.DirectoryCount.ToString("N0");
            StatusText.Text = $"Scanning {p.CurrentPath}";
            RaiseTitleChanged();
        });

        var completed = false;
        try
        {
            _result = await new DiskScanner().ScanAsync(requested, progress, _scanCancellation.Token);
            _hasScopedRefreshes = false;
            ShowResult(_result);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan stopped. Partial results were discarded.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan could not be completed.";
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
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

    public async Task<bool> RefreshCurrentFolderAsync()
    {
        if (_isScanning || _result is null) return false;

        var target = GetRefreshTargetNode();
        if (target is null)
        {
            MessageBox.Show(Window.GetWindow(this), "Select or zoom into a folder first.", "No folder selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var selectedPathBefore = _selectedNode?.Path;
        var viewPathBefore = Treemap.Root?.Path;

        _isScanning = true;
        _scanCancellation = new CancellationTokenSource();
        SetScanningState(true);
        StatusText.Text = $"Updating {target.Path}";
        ElapsedText.Text = "";
        RaiseTitleChanged();

        var progress = new Progress<ScanProgress>(p =>
        {
            StatusText.Text = $"Updating {p.CurrentPath}";
            RaiseTitleChanged();
        });

        var completed = false;
        try
        {
            var subtreeResult = await new DiskScanner().ScanAsync(target.Path, progress, _scanCancellation.Token);
            _result = ReplaceSubtreeAndRebuildResult(target, subtreeResult);
            _hasScopedRefreshes = !string.Equals(_result.Root.Path, subtreeResult.Root.Path, StringComparison.OrdinalIgnoreCase);
            ShowResult(_result, $"Folder updated: {target.Path}", $"Updated in {FormatDuration(subtreeResult.Duration)}");
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
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Folder update failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
            fileCount = _result.FileCount,
            folderCount = _result.DirectoryCount,
            skippedReparsePoints = _result.SkippedReparsePoints,
            issueCount = _result.Issues.Count,
            topLevel = _result.Root.Children.Where(x => x.Size > 0).Take(50).Select(x => ToAgentNode(x, 0, 0)),
            largestFiles = _result.LargestFiles.Take(100).Select(x => ToAgentNode(x, 0, 0)),
            suggestions = _result.Suggestions.Select(ToAgentSuggestion),
            safety = "Suggestions are advisory. PC Space Map does not delete files."
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
        var files = _result.LargestFiles
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
        RootPathBox.Text = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "C:\\";
    }

    private void ShowResult(ScanResult result, string? statusOverride = null, string? elapsedOverride = null)
    {
        TotalSizeText.Text = result.Root.SizeText;
        FileCountText.Text = result.FileCount.ToString("N0");
        FolderCountText.Text = result.DirectoryCount.ToString("N0");
        CleanupSizeText.Text = SizeFormatter.Format(result.Suggestions.Sum(x => x.Size));
        ElapsedText.Text = elapsedOverride ?? $"Completed in {FormatDuration(result.Duration)}";
        StatusText.Text = statusOverride ?? $"Inventory complete: {result.Root.Path}";

        LargestFilesGrid.ItemsSource = result.LargestFiles;
        CleanupGrid.ItemsSource = result.Suggestions;
        IssuesGrid.ItemsSource = result.Issues;
        IssueSummaryText.Text = BuildIssueSummary(result);
        ExportButton.IsEnabled = true;
        MainTabs.SelectedIndex = 0;
        ZoomTo(result.Root);
        SelectNode(result.Root);
        RaiseTitleChanged();
    }

    private void ClearResults()
    {
        _result = null;
        _selectedNode = null;
        Treemap.Root = null;
        LargestFilesGrid.ItemsSource = null;
        CleanupGrid.ItemsSource = null;
        IssuesGrid.ItemsSource = null;
        BreadcrumbPanel.Children.Clear();
        CleanupSizeText.Text = "—";
        ElapsedText.Text = "";
        ExportButton.IsEnabled = false;
        _hasScopedRefreshes = false;
        SelectNode(null);
        RaiseTitleChanged();
    }

    private void SetScanningState(bool scanning)
    {
        ScanButton.IsEnabled = !scanning;
        BrowseButton.IsEnabled = !scanning;
        RootPathBox.IsEnabled = !scanning;
        RefreshFolderButton.IsEnabled = !scanning && GetRefreshTargetNode() is not null;
        RefreshSelectedButton.IsEnabled = !scanning && GetRefreshTargetNode() is not null;
        CancelButton.IsEnabled = scanning;
        ScanProgressBar.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        if (scanning) StatusText.Text = "Starting scan…";
        RaiseTitleChanged();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await StartScanAsync();
    private async void RefreshFolderButton_Click(object sender, RoutedEventArgs e) => await RefreshCurrentFolderAsync();

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
            RootPathBox.Text = dialog.FolderName;
            RaiseTitleChanged();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "Stopping after the current folder…";
        _scanCancellation?.Cancel();
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
            await CsvExporter.ExportAsync(_result.Root, dialog.FileName);
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

    private void Treemap_NodeSelected(object sender, ScanNode node) => SelectNode(node);
    private void Treemap_ZoomRequested(object sender, ScanNode node) => ZoomTo(node);

    private void SelectNode(ScanNode? node)
    {
        _selectedNode = node;
        SelectedNameText.Text = node?.Name ?? "Nothing selected";
        SelectedSizeText.Text = node?.SizeText ?? "—";
        SelectedContentsText.Text = BuildSelectedSummary(node);
        SelectedPathText.Text = node?.Path ?? "—";
        OpenSelectedButton.IsEnabled = node is not null;
        ZoomSelectedButton.IsEnabled = node?.IsDirectory == true;
        RefreshFolderButton.IsEnabled = !_isScanning && GetRefreshTargetNode() is not null;
        RefreshSelectedButton.IsEnabled = !_isScanning && GetRefreshTargetNode() is not null;
    }

    private void ZoomTo(ScanNode node)
    {
        if (!node.IsDirectory) return;
        Treemap.Root = node;
        SelectNode(node);
        BuildBreadcrumbs(node);
    }

    private void BuildBreadcrumbs(ScanNode current)
    {
        BreadcrumbPanel.Children.Clear();
        var chain = new Stack<ScanNode>();
        for (var node = current; node is not null; node = node.Parent)
            chain.Push(node);

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
    }

    private void OpenSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is not null) OpenInExplorer(_selectedNode.Path, !_selectedNode.IsDirectory);
    }

    private void ZoomSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode?.IsDirectory == true) ZoomTo(_selectedNode);
    }

    private void LargestFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LargestFilesGrid.SelectedItem is ScanNode node) OpenInExplorer(node.Path, selectFile: true);
    }

    private void CleanupGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CleanupGrid.SelectedItem is CleanupSuggestion suggestion) OpenInExplorer(suggestion.SamplePath, selectFile: true);
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
        var title = string.IsNullOrWhiteSpace(basePath) ? "New scan" : ToShortTabLabel(basePath);
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

        return node.ContentsText;
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

        if (Treemap.Root?.IsDirectory == true)
            return Treemap.Root;

        return _result.Root.IsDirectory ? _result.Root : null;
    }

    private ScanResult ReplaceSubtreeAndRebuildResult(ScanNode target, ScanResult subtreeResult)
    {
        if (_result is null)
            return subtreeResult;

        ScanNode updatedRoot;
        if (target.Parent is null)
        {
            updatedRoot = subtreeResult.Root;
        }
        else
        {
            var parent = target.Parent;
            var index = parent.Children.IndexOf(target);
            if (index < 0)
                throw new InvalidOperationException("The selected folder is no longer present in the current inventory.");

            subtreeResult.Root.Parent = parent;
            parent.Children[index] = subtreeResult.Root;
            RecalculateDirectory(parent);

            for (var ancestor = parent.Parent; ancestor is not null; ancestor = ancestor.Parent)
                RecalculateDirectory(ancestor);

            updatedRoot = _result.Root;
        }

        var cleanup = new CleanupAnalyzer();
        var largestFiles = new List<ScanNode>();
        foreach (var node in updatedRoot.DescendantsAndSelf())
        {
            if (node.IsDirectory) continue;
            largestFiles.Add(node);
            cleanup.Consider(node.Path, node.Size, node.LastWriteUtc);
        }

        largestFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (largestFiles.Count > 5000)
            largestFiles.RemoveRange(5000, largestFiles.Count - 5000);

        var retainedIssues = _result.Issues.Where(x => !IsSameOrBelow(x.Path, target.Path)).ToList();
        retainedIssues.AddRange(subtreeResult.Issues);

        return new ScanResult
        {
            Root = updatedRoot,
            LargestFiles = largestFiles,
            Suggestions = cleanup.Build(),
            Issues = retainedIssues,
            Duration = subtreeResult.Duration,
            FileCount = updatedRoot.FileCount,
            DirectoryCount = updatedRoot.DirectoryCount + 1,
            SkippedReparsePoints = _result.SkippedReparsePoints
        };
    }

    private void RestoreNavigation(string? viewPathBefore, string? selectedPathBefore)
    {
        var viewNode = !string.IsNullOrWhiteSpace(viewPathBefore) ? FindNode(viewPathBefore) : null;
        if (viewNode?.IsDirectory == true)
            ZoomTo(viewNode);
        else if (_result is not null)
            ZoomTo(_result.Root);

        var selectedNode = !string.IsNullOrWhiteSpace(selectedPathBefore) ? FindNode(selectedPathBefore) : null;
        if (selectedNode is not null)
            SelectNode(selectedNode);
    }

    private static void RecalculateDirectory(ScanNode node)
    {
        if (!node.IsDirectory)
            return;

        foreach (var child in node.Children.Where(x => x.IsDirectory))
            RecalculateDirectory(child);

        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        node.Size = node.Children.Sum(x => x.Size);
        node.FileCount = node.Children.Sum(x => x.FileCount);
        node.DirectoryCount = node.Children.Sum(x => x.IsDirectory ? x.DirectoryCount + 1 : 0);
    }

    private static bool TryGetDriveUsage(string path, out string usage)
    {
        usage = "";
        try
        {
            var normalized = NormalizePath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(root) || !string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
                return false;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return false;

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
        if (_result is null) return null;
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

    private static object ToAgentNode(ScanNode node, int depth, int limit)
    {
        var children = limit > 0 ? node.Children.Where(x => x.Size > 0).Take(limit).ToList() : [];
        var includedSize = children.Sum(x => x.Size);
        return new
        {
            node.Name,
            node.Path,
            kind = node.IsDirectory ? "folder" : "file",
            node.Size,
            node.SizeText,
            node.FileCount,
            node.DirectoryCount,
            node.ModifiedText,
            childCount = node.Children.Count,
            returnedChildCount = children.Count,
            omittedChildCount = Math.Max(0, node.Children.Count - children.Count),
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
        var normalizedBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
