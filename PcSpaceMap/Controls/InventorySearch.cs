using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PcSpaceMap.Models;

namespace PcSpaceMap.Controls;

public partial class ScanWorkspaceControl
{
    private const int SearchPageSize = 200;
    private FileFilter? _listNameFilter;
    private FileFilter? _searchName;
    private ScanResult? _searchInventory;
    private int _searchOffset;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _contentsCancellation;
    private CancellationTokenSource? _cleanupCancellation;

    private void Workspace_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        MainTabs.SelectedItem = SearchTab;
        SearchNameBox.Focus();
        SearchNameBox.SelectAll();
        e.Handled = true;
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_optionsReady || !ReferenceEquals(e.OriginalSource, MainTabs) || !ReferenceEquals(MainTabs.SelectedItem, SearchTab)) return;
        if (_searchInventory != _result && _searchName is not null) _ = RunSearchAsync(0);
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await StartSearchAsync();

    private async void SearchName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await StartSearchAsync();
    }

    private Task StartSearchAsync()
    {
        _searchName = ToNameFilter(SearchNameBox.Text);
        return RunSearchAsync(0);
    }

    private async void SearchPage_Click(object sender, RoutedEventArgs e) =>
        await RunSearchAsync(Math.Max(0, _searchOffset + (ReferenceEquals(sender, SearchNextButton) ? SearchPageSize : -SearchPageSize)),
            _searchInventory);

    private async Task RunSearchAsync(int offset, ScanResult? previousPage = null)
    {
        if (_workspaceClosed) return;
        _searchCancellation?.Cancel();
        _searchInventory = null;
        SearchPreviousButton.IsEnabled = SearchNextButton.IsEnabled = false;
        SearchResultsGrid.ItemsSource = null;
        if ((previousPage ?? _result) is not { Inventory: { } store } inventory)
        {
            SearchStatusText.Text = "Scan a drive or open a saved inventory to search.";
            return;
        }
        if (_searchName is not { } filter)
        {
            SearchStatusText.Text = "Enter part of a file or folder name, or a wildcard such as *.zip.";
            return;
        }

        SearchStatusText.Text = $"Searching {inventory.Root.Path}…";
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var page = await Task.Run(() => store.SearchNames(inventory.SnapshotId, inventory.Root.Path, filter,
                offset, SearchPageSize + 1, token), token);
            if (token.IsCancellationRequested) return;
            var rows = page.Take(SearchPageSize).ToArray();
            _searchInventory = inventory;
            _searchOffset = offset;
            SearchResultsGrid.ItemsSource = rows;
            SearchPreviousButton.IsEnabled = offset > 0;
            SearchNextButton.IsEnabled = page.Count > SearchPageSize;
            SearchStatusText.Text = rows.Length == 0 ? $"No matches in {inventory.Root.Path}."
                : $"Matches {offset + 1:N0}–{offset + rows.Length:N0} in {inventory.Root.Path}. Enter opens a folder in the map or reveals a file in Explorer.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) SearchStatusText.Text = "Search failed. " + ex.Message; }
        finally { if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null; }
    }

    private async void ApplyListFilter_Click(object sender, RoutedEventArgs e) => await ApplyListNameFilterAsync(FilterNameBox.Text);
    private async void ClearListFilter_Click(object sender, RoutedEventArgs e) => await ApplyListNameFilterAsync("");
    private async void UseSearchFilter_Click(object sender, RoutedEventArgs e) => await ApplyListNameFilterAsync(SearchNameBox.Text);

    private async void ListFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplyListNameFilterAsync(FilterNameBox.Text);
    }

    private static FileFilter? ToNameFilter(string value)
    {
        var name = value.Trim();
        return name.Length > 0 ? new FileFilter(name, 0, null) : null;
    }

    private async Task ApplyListNameFilterAsync(string name)
    {
        _listNameFilter = ToNameFilter(name);
        FilterNameBox.Text = _listNameFilter?.Name ?? "";
        ListNameFilterStatus.Text = _listNameFilter is null ? "" : "Applied to inventory lists and space map";
        var map = (_mapFilter ?? new FileFilter("", 0, null)) with { Name = FilterNameBox.Text };
        _mapFilter = map is { Name.Length: 0, MinimumBytes: 0, ModifiedBefore: null, ModifiedSince: null } ? null : map;
        _contentsOffset = 0;
        if (_result is { } inventory) RefreshStoredLists(inventory);
        await Task.WhenAll(RefreshFilterAsync(), Treemap.Root is { } node ? ShowContentsAsync(node, _result) : Task.CompletedTask);
    }

    private void RefreshStoredLists(ScanResult inventory)
    {
        var filter = _listNameFilter;
        bool Matches(string path) => filter is null || filter.MatchesName(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
        IssuesGrid.ItemsSource = inventory.Issues.Where(issue => Matches(issue.Path)).ToArray();
        ChangesGrid.ItemsSource = inventory.Changes.Where(change => Matches(change.Path)).ToArray();
        _ = RefreshCleanupListAsync(inventory, filter);
    }

    private async Task RefreshCleanupListAsync(ScanResult inventory, FileFilter? filter)
    {
        const string advisory = "Suggestions are grouped and advisory. Enter or double-click to inspect the largest example.";
        _cleanupCancellation?.Cancel();
        if (filter is null || inventory.Inventory is not { } store)
        {
            CleanupGrid.ItemsSource = inventory.Suggestions;
            CleanupDescription.Text = advisory;
            return;
        }

        CleanupGrid.ItemsSource = null;
        CleanupDescription.Text = "Finding cleanup candidates with matching names…";
        using var cancellation = new CancellationTokenSource();
        _cleanupCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var rows = await Task.Run(() => store.FilterCleanup(inventory.SnapshotId, inventory.Root.Path, inventory.CompletedUtc,
                filter, token), token);
            if (token.IsCancellationRequested || _result != inventory) return;
            CleanupGrid.ItemsSource = rows;
            CleanupDescription.Text = rows.Count == 0 ? "No cleanup candidates match the name filter."
                : "Totals include only candidates with matching names. " + advisory;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) CleanupDescription.Text = "Cleanup filter failed. " + ex.Message; }
        finally { if (ReferenceEquals(_cleanupCancellation, cancellation)) _cleanupCancellation = null; }
    }
}
