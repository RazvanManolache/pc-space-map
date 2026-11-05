using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using PcSpaceMap.Models;

namespace PcSpaceMap.Controls;

public partial class ScanWorkspaceControl
{
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private FileFilter? _mapFilter;
    private long _filterVersion;
    private CancellationTokenSource? _filterCancellation;
    private (ScanResult Inventory, string MapPath, string ListPath, FileFilter Filter)? _largestFilterKey;

    private void ResetMapHistory()
    {
        _back.Clear();
        _forward.Clear();
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
    }

    private void CommitMapNavigation(string? from, string to, bool remember, int direction)
    {
        if (from is null || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        if (direction != 0)
        {
            var source = direction < 0 ? _back : _forward;
            if (source.TryPeek(out var pending) && string.Equals(pending, to, StringComparison.OrdinalIgnoreCase))
            {
                source.Pop();
                (direction < 0 ? _forward : _back).Push(from);
            }
        }
        else if (remember)
        {
            _back.Push(from);
            _forward.Clear();
        }
        UpdateHistoryButtons();
    }

    private bool TravelHistory(int direction)
    {
        var source = direction < 0 ? _back : _forward;
        while (source.TryPeek(out var path))
        {
            if (FindNode(path) is { IsDirectory: true } node)
            {
                ZoomTo(node, remember: false, historyDirection: direction);
                return true;
            }
            source.Pop();
        }
        UpdateHistoryButtons();
        return false;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => TravelHistory(-1);
    private void Forward_Click(object sender, RoutedEventArgs e) => TravelHistory(1);
    private void LessDetail_Click(object sender, RoutedEventArgs e) => SetMapDetail(Treemap.Detail - 1);
    private void MoreDetail_Click(object sender, RoutedEventArgs e) => SetMapDetail(Treemap.Detail + 1);
    private void FreeSpace_Click(object sender, RoutedEventArgs e) => RefreshFreeTile();

    private void SetMapDetail(int level)
    {
        level = Math.Clamp(level, 0, TreemapControl.MaxDepth);
        Treemap.SetDetail(level);
        DetailText.Text = $"Detail {level}";
        LessDetailButton.IsEnabled = level > 0;
        MoreDetailButton.IsEnabled = level < TreemapControl.MaxDepth;
    }

    private void SpaceMap_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase or ComboBox) return;
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Back && modifiers is ModifierKeys.None or ModifierKeys.Shift)
            e.Handled = TravelHistory(modifiers == ModifierKeys.Shift ? 1 : -1);
        else if ((modifiers & ModifierKeys.Control) != 0 && e.Key is Key.Add or Key.OemPlus or Key.Subtract or Key.OemMinus)
        {
            SetMapDetail(Treemap.Detail + (e.Key is Key.Add or Key.OemPlus ? 1 : -1));
            e.Handled = true;
        }
    }

    private void RefreshFreeTile()
    {
        var root = Treemap.Root;
        var drive = root is null ? null : GetReadyDriveRoot(root.Path);
        FreeSpaceBox.IsEnabled = drive is not null;
        try
        {
            if (drive is not null && FreeSpaceBox.IsChecked == true)
            {
                Treemap.SetFreeSpace(drive.AvailableFreeSpace, drive.TotalSize);
                return;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Treemap.SetFreeSpace(0, 0);
    }

    private void Treemap_ShellMenuRequested(object sender, (ScanNode Node, Point Position) request)
    {
        try
        {
            if (PresentationSource.FromVisual(Treemap) is not HwndSource owner) return;
            var point = Treemap.PointToScreen(request.Position);
            WindowsItemMenu.Show(owner, request.Node.Path, (int)point.X, (int)point.Y);
        }
        catch (Exception ex) { StatusText.Text = "Windows menu unavailable: " + ex.Message; }
    }

    private async void FilterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplyMapFilterAsync();
    }

    private async void ApplyFilter_Click(object sender, RoutedEventArgs e) => await ApplyMapFilterAsync();
    private async void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        FilterSizeBox.Clear();
        FilterAgeBox.Clear();
        FilterAgeEndBox.Clear();
        await ApplyMapFilterAsync();
    }

    private void FilterResults_Click(object sender, RoutedEventArgs e) =>
        MainTabs.SelectedItem = MainTabs.Items.OfType<TabItem>().First(tab => Equals(tab.Header, "Largest files"));

    private async Task<bool> ApplyMapFilterAsync()
    {
        var name = _listNameFilter?.Name ?? "";
        var size = FilterSizeBox.Text.Trim();
        decimal mb = 0;
        if (size.Length > 0 && (!decimal.TryParse(size, NumberStyles.Number, CultureInfo.CurrentCulture, out mb)
                || mb < 0 || mb > long.MaxValue / 1048576m))
        {
            FilterStatusText.Text = "Enter a valid size.";
            return false;
        }
        var between = FilterAgeModeBox.SelectedIndex == 2;
        if (!TryAgeCutoffs(FilterAgeBox.Text.Trim(), between ? FilterAgeEndBox.Text.Trim() : null,
                olderThan: FilterAgeModeBox.SelectedIndex == 1, out var before, out var since))
        {
            FilterStatusText.Text = between ? "Enter two different ages in whole days." : "Enter an age in whole days.";
            return false;
        }
        _mapFilter = name.Length == 0 && mb == 0 && before is null && since is null ? null
            : new FileFilter(name, (long)decimal.Ceiling(mb * 1048576m), before, since);
        await RefreshFilterAsync();
        return true;
    }

    private static bool TryAgeCutoffs(string first, string? second, bool olderThan, out DateTime? before, out DateTime? since)
    {
        before = since = null;
        if (first.Length == 0 && string.IsNullOrEmpty(second)) return true;
        static bool ReadDays(string text, out int days) => int.TryParse(text, out days) && days is >= 0 and <= 365_000;
        if (!ReadDays(first, out var start)) return false;
        var now = DateTime.UtcNow;
        if (second is not null)
        {
            if (!ReadDays(second, out var end) || start == end) return false;
            before = now.AddDays(-Math.Min(start, end));
            since = now.AddDays(-Math.Max(start, end));
        }
        else if (start > 0)
        {
            if (olderThan) before = now.AddDays(-start);
            else since = now.AddDays(-start);
        }
        return true;
    }

    private async Task RefreshFilterAsync(bool keepMap = false)
    {
        const int limit = 500;
        var filter = _mapFilter;
        var inventory = _result;
        var mapPath = Treemap.Root?.Path;
        var listPath = filter is { MinimumBytes: 0, ModifiedBefore: null, ModifiedSince: null } ? inventory?.Root.Path : mapPath;
        if (inventory is not null && mapPath is not null && listPath is not null && filter is not null
            && _largestFilterKey == (inventory, mapPath, listPath, filter)) return;
        var version = ++_filterVersion;
        _filterCancellation?.Cancel();
        _filterCancellation = null;
        _largestFilterKey = null;
        keepMap = keepMap && Treemap.HasFilteredView;
        if (!keepMap) FilterResultsButton.IsEnabled = false;
        if (filter is null || mapPath is null || listPath is null || inventory?.Inventory is not { } store)
        {
            Treemap.SetFilteredView(null);
            UpdateSelectedSummary(_selectedNode);
            FilterStatusText.Text = filter is null ? "" : "Open an inventory to filter";
            LargestFilesGrid.ItemsSource = inventory?.LargestFiles;
            LargestFilesText.Text = "The 5,000 largest files found. Press Enter or double-click a row to reveal it in Explorer.";
            return;
        }

        FilterStatusText.Text = keepMap ? "Refreshing matches…" : "Finding matches…";
        if (!keepMap)
        {
            Treemap.SetFilteredView(null, "Finding matching files…");
            UpdateSelectedSummary(_selectedNode);
            LargestFilesText.Text = "Finding matching files…";
            LargestFilesGrid.ItemsSource = null;
        }
        using var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var (filteredMap, mapMatches, listMatches) = await Task.Run(() =>
            {
                var map = store.Largest(inventory.SnapshotId, mapPath, limit + 1, filter, token);
                var list = mapPath.Equals(listPath, StringComparison.OrdinalIgnoreCase)
                    ? map : store.Largest(inventory.SnapshotId, listPath, limit + 1, filter, token);
                return (new FilteredTreemap(mapPath, map.Take(limit), token), map, list);
            }, token);
            if (version != _filterVersion || _result != inventory) return;
            var mapCount = mapMatches.Count;
            Treemap.SetFilteredView(filteredMap, mapCount == 0 ? "No matching files in this folder."
                : "Matching files are empty. Use the list to see their paths.");
            if (_selectedNode is { } selected && (selected.IsDirectory
                ? filteredMap.FindFolder(selected.Path) is null
                : !mapMatches.Take(limit).Any(file => file.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase))))
                SelectNode(Treemap.Root);
            else UpdateSelectedSummary(_selectedNode);
            LargestFilesGrid.ItemsSource = listMatches.Take(limit).ToArray();
            _largestFilterKey = (inventory, mapPath, listPath, filter);
            var mapSummary = mapCount > limit ? $"largest {limit} matches"
                : $"{mapCount:N0} {(mapCount == 1 ? "match" : "matches")}";
            FilterStatusText.Text = $"Map · {mapSummary} · {filteredMap.Root.SizeText} shown";
            var listCount = listMatches.Count > limit ? $"Showing the largest {limit} matches" : $"{listMatches.Count:N0} matching files";
            LargestFilesText.Text = $"{listCount} in {listPath}. Press Enter or double-click a row to reveal it in Explorer.";
            FilterResultsButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version != _filterVersion || _result != inventory) return;
            FilterStatusText.Text = keepMap ? "Refresh failed · showing previous map" : "Filter failed";
            if (!keepMap)
            {
                Treemap.SetFilteredView(null, "Could not filter the map. Apply to retry or clear the filter.");
                UpdateSelectedSummary(_selectedNode);
            }
            LargestFilesText.Text = "Filter failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_filterCancellation, cancellation)) _filterCancellation = null;
        }
    }

    private static DriveInfo? GetReadyDriveRoot(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || !string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive : null;
        }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
