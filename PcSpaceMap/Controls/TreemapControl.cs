using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PcSpaceMap.Models;

namespace PcSpaceMap.Controls;

public sealed class TreemapControl : FrameworkElement
{
    internal const int MaxDepth = 8;

    private static readonly Brush MapBackground = BrushFor(0x08101D);
    private static readonly Brush OtherFill = BrushFor(0x28374A);
    private static readonly Brush FreeFill = BrushFor(0x254C56);
    private static readonly Brush LightText = BrushFor(0xF5F9FF);
    private static readonly Brush DarkText = BrushFor(0x111C2C);
    private static readonly Brush HintText = BrushFor(0xA9BED7);
    private static readonly Pen TileBorder = PenFor(0x0B1524, 1);
    private static readonly Pen FolderBorder = PenFor(0x849BB7, 1);
    private static readonly Pen SelectionShadow = PenFor(0x07101C, 5);
    private static readonly Pen SelectionStroke = PenFor(0xF7FCFF, 2.5);
    private static readonly Typeface LabelTypeface = new("Segoe UI Semibold");
    private static readonly Typeface SizeTypeface = new("Segoe UI");

    private static readonly Brush[] FolderColors =
        Palette(0x23497F, 0x654079, 0x225B54, 0x804624, 0x365C2B, 0x783C51, 0x1B5875, 0x604A87);
    private static readonly Brush[] FileColors =
        Palette(0x7CC5FF, 0xBAA6FF, 0x63DBC6, 0xFFBD7F, 0xBBDA84, 0xF29BBB, 0x8FD9F4, 0xE4C4FF);

    private readonly List<HitRegion> _hitRegions = [];
    private IReadOnlyDictionary<ScanNode, IReadOnlyList<ScanNode>>? _preview;
    private ScanNode? _root;
    private HitRegion? _hovered;
    private string? _selectedPath;
    private ScanNode? _firstClick;
    private FilteredTreemap? _filteredView;
    private string? _filteredMessage;
    private (long Bytes, long Capacity) _free;
    internal int Detail { get; private set; } = 5;

    public ScanNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            _preview = null;
            _firstClick = null;
            InvalidateVisual();
        }
    }

    internal string? SelectedPath
    {
        set
        {
            if (string.Equals(_selectedPath, value, StringComparison.OrdinalIgnoreCase)) return;
            _selectedPath = value;
            InvalidateVisual();
        }
    }

    internal void SetPreview(IReadOnlyDictionary<ScanNode, IReadOnlyList<ScanNode>> preview)
    {
        _preview = preview;
        InvalidateVisual();
    }

    internal void SetDetail(int level) { Detail = level; InvalidateVisual(); }
    internal void SetFilteredView(FilteredTreemap? view, string? emptyMessage = null)
    {
        _filteredView = view;
        _filteredMessage = emptyMessage;
        _firstClick = null;
        InvalidateVisual();
    }
    internal ScanNode? FilteredFolder(string path) => _filteredView?.FindFolder(path);
    internal bool HasFilteredView => _filteredView is not null;
    internal void SetFreeSpace(long bytes, long capacity) { _free = (bytes, capacity); InvalidateVisual(); }
    public event EventHandler<ScanNode>? NodeSelected;
    public event EventHandler<ScanNode>? ZoomRequested;
    public event EventHandler<(ScanNode Node, Point Position)>? ShellMenuRequested;

    public TreemapControl()
    {
        SnapsToDevicePixels = true;
        Focusable = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _hitRegions.Clear();

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(MapBackground, null, bounds);
        if (bounds.Width < 20 || bounds.Height < 20) return;

        var map = Deflate(bounds, 2);
        if (_filteredView?.Root is { Size: > 0 } filtered)
            DrawGroup(drawingContext, filtered, filtered.Children, map, 0);
        else if (_filteredMessage is not null)
            DrawCenteredMessage(drawingContext, _filteredMessage, bounds);
        else if (_root is null || !_root.ChildrenLoaded || (_root.Size <= 0 && _free.Bytes <= 0))
        {
            DrawCenteredMessage(drawingContext, _root is null ? "Choose a folder or drive, then start a scan."
                : "No sized files were found in this location.", bounds);
            return;
        }

        else
        {
            if (_free is { Bytes: > 0, Capacity: > 0 })
            {
                var width = map.Width * Math.Min(1, (double)_free.Bytes / _free.Capacity);
                DrawTile(drawingContext, new Tile(null, _free.Bytes, true),
                    Deflate(new Rect(map.Right - width, map.Y, width, map.Height), 0.5), 0);
                map.Width -= width;
            }
            DrawGroup(drawingContext, _root, _root.Children, map, 0);
        }

        var selected = _hitRegions.FirstOrDefault(region => region.Tile.Node is { } node
            && node.Path.Equals(_selectedPath, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            var outline = Deflate(selected.Bounds, 1);
            drawingContext.DrawRectangle(null, SelectionShadow, outline);
            drawingContext.DrawRectangle(null, SelectionStroke, outline);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTestRegion(e.GetPosition(this));
        if (ReferenceEquals(hit, _hovered)) return;
        _hovered = hit;
        Cursor = hit?.Tile.Node is not null ? Cursors.Hand : Cursors.Arrow;
        ToolTip = hit?.Tile.Node is { } node
            ? $"{node.Path}\n{node.SizeText} · " + (node.IsDirectory && _filteredView is not null
                ? $"{node.FileCount:N0} matching files shown\nDouble-click to zoom."
                : node.ContentsText + (node.IsDirectory ? "\nDouble-click to zoom." : ""))
            : hit?.Tile.Free == true ? $"{SizeFormatter.Format(hit.Tile.Size)} free on this drive"
            : hit is not null ? $"Other items · {SizeFormatter.Format(hit.Tile.Size)}\nZoom into the containing folder for more detail." : null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        ToolTip = null;
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2 && _firstClick?.IsDirectory == true)
        {
            ZoomRequested?.Invoke(this, _firstClick);
            return;
        }
        var node = HitTestRegion(e.GetPosition(this))?.Tile.Node;
        _firstClick = node;
        if (node is null) return;
        NodeSelected?.Invoke(this, node);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (HitTestRegion(e.GetPosition(this))?.Tile.Node is not { } node) return;
        Focus();
        NodeSelected?.Invoke(this, node);
        e.Handled = true;
        ShellMenuRequested?.Invoke(this, (node, e.GetPosition(this)));
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var selected = _hitRegions.Find(hit =>
            string.Equals(hit.Tile.Node?.Path, _selectedPath, StringComparison.OrdinalIgnoreCase));
        var node = selected?.Tile.Node ?? Root;
        if (node is null) return;

        switch (e.Key)
        {
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var origin = selected is null ? new Point() : Center(selected.Bounds);
                var next = selected is null ? _hitRegions.FirstOrDefault(hit => hit.Tile.Node is not null)
                    : _hitRegions.Where(hit => hit.Tile.Node is not null && hit != selected)
                        .Select(hit => (Hit: hit, Delta: Center(hit.Bounds) - origin))
                        .Select(hit => (hit.Hit, Forward: e.Key switch
                        {
                            Key.Left => -hit.Delta.X, Key.Right => hit.Delta.X,
                            Key.Up => -hit.Delta.Y, _ => hit.Delta.Y
                        }, Distance: hit.Delta.LengthSquared))
                        .Where(hit => hit.Forward > 0)
                        .OrderBy(hit => hit.Distance / hit.Forward)
                        .Select(hit => hit.Hit).FirstOrDefault();
                if (next?.Tile.Node is { } target) NodeSelected?.Invoke(this, target);
                break;
            case Key.Enter when node.IsDirectory:
                ZoomRequested?.Invoke(this, node);
                break;
            case Key.Apps:
            case Key.F10 when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
            case Key.System when e.SystemKey == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                ShellMenuRequested?.Invoke(this, (node, selected is null ? new Point(8, 8) : Center(selected.Bounds)));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static Point Center(Rect bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    private void DrawGroup(DrawingContext dc, ScanNode folder, IReadOnlyList<ScanNode> children, Rect area, int depth)
    {
        var tiles = children.Where(node => node.Size > 0).OrderByDescending(node => node.Size)
            .Select(node => new Tile(node, node.Size)).ToList();
        var shown = tiles.Sum(tile => tile.Size);
        if (shown < folder.Size) tiles.Add(new Tile(null, folder.Size - shown));
        var weights = new long[tiles.Count + 1];
        for (var i = 0; i < tiles.Count; i++) weights[i + 1] = weights[i] + tiles[i].Size;
        LayoutGroup(dc, tiles, weights, 0, tiles.Count, area, depth);
    }

    private void LayoutGroup(DrawingContext dc, IReadOnlyList<Tile> tiles, long[] weights, int start, int end, Rect area, int depth)
    {
        if (start >= end || area.Width < 2 || area.Height < 2) return;
        if (end - start == 1)
        {
            DrawTile(dc, tiles[start], Deflate(area, 0.5), depth);
            return;
        }

        var total = weights[end] - weights[start];
        var split = start + 1;
        while (split < end - 1 && weights[split + 1] - weights[start] <= total / 2) split++;
        var ratio = (double)(weights[split] - weights[start]) / total;
        if (area.Width >= area.Height)
        {
            var width = area.Width * ratio;
            LayoutGroup(dc, tiles, weights, start, split, new Rect(area.X, area.Y, width, area.Height), depth);
            LayoutGroup(dc, tiles, weights, split, end, new Rect(area.X + width, area.Y, area.Width - width, area.Height), depth);
        }
        else
        {
            var height = area.Height * ratio;
            LayoutGroup(dc, tiles, weights, start, split, new Rect(area.X, area.Y, area.Width, height), depth);
            LayoutGroup(dc, tiles, weights, split, end, new Rect(area.X, area.Y + height, area.Width, area.Height - height), depth);
        }
    }

    private void DrawTile(DrawingContext dc, Tile tile, Rect area, int depth)
    {
        if (area.Width < 1 || area.Height < 1) return;
        var node = tile.Node;
        var folder = node?.IsDirectory == true;
        IReadOnlyList<ScanNode>? children = null;
        if (folder && node is not null && depth < Detail)
        {
            if (_filteredView is not null) children = node.Children;
            else _preview?.TryGetValue(node, out children);
        }
        var showChildren = children is { Count: > 0 } && area.Width >= 42 && area.Height >= 33;
        _hitRegions.Add(new HitRegion(area, tile));
        var fill = tile.Free ? FreeFill : node is null ? OtherFill
            : folder ? FolderColors[ColorIndex(node.Path, FolderColors.Length)]
            : FileColors[ColorIndex(System.IO.Path.GetExtension(node.Name), FileColors.Length)];
        dc.DrawRectangle(fill, folder ? FolderBorder : TileBorder, area);

        var name = tile.Free ? "Free space" : node?.Name ?? "Other items";
        var size = SizeFormatter.Format(tile.Size);
        if (folder && node is not null)
        {
            var header = area.Width >= 36 && area.Height >= 17 ? 16 : 0;
            if (header > 0)
                DrawLabel(dc, area.Width >= 78 ? $"{name}  ·  {size}" : name,
                    new Rect(area.X + 3, area.Y + 1, area.Width - 6, header - 1), LightText, LabelTypeface, 10);

            if (showChildren)
            {
                var inner = new Rect(area.X + 2, area.Y + header, area.Width - 4, area.Height - header - 2);
                DrawGroup(dc, node, children!, inner, depth + 1);
            }
        }
        else if (area.Width >= 30 && area.Height >= 17)
        {
            var text = node is null ? LightText : DarkText;
            if (tile.Free && area.Width < 92 && area.Height >= 120)
            {
                dc.PushTransform(new RotateTransform(90, area.X + 15, area.Y + 5));
                DrawLabel(dc, $"{name} · {size}", new Rect(area.X + 15, area.Y + 5, area.Height - 10, 15),
                    text, LabelTypeface, 10);
                dc.Pop();
            }
            else
            {
                DrawLabel(dc, name, new Rect(area.X + 3, area.Y + 1, area.Width - 6, 15), text, LabelTypeface, 10);
                if (area.Width >= 42 && area.Height >= 31)
                    DrawLabel(dc, size, new Rect(area.X + 3, area.Y + 15, area.Width - 6, 15), text, SizeTypeface, 10);
            }
        }
    }

    private void DrawLabel(DrawingContext dc, string label, Rect area, Brush brush, Typeface typeface, double size)
    {
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = area.Width,
            MaxTextHeight = area.Height,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        dc.DrawText(text, area.TopLeft);
    }

    private HitRegion? HitTestRegion(Point point)
    {
        for (var i = _hitRegions.Count - 1; i >= 0; i--)
            if (_hitRegions[i].Bounds.Contains(point)) return _hitRegions[i];
        return null;
    }

    private static Rect Deflate(Rect rect, double amount) => new(
        rect.X + amount, rect.Y + amount, Math.Max(0, rect.Width - amount * 2), Math.Max(0, rect.Height - amount * 2));

    private static int ColorIndex(string value, int count)
    {
        uint hash = 2166136261;
        foreach (var character in value)
            hash = unchecked((hash ^ char.ToUpperInvariant(character)) * 16777619);
        return (int)(hash % count);
    }

    private static Brush[] Palette(params int[] colors) => colors.Select(BrushFor).ToArray();

    private static Brush BrushFor(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    private static Pen PenFor(int rgb, double thickness)
    {
        var pen = new Pen(BrushFor(rgb), thickness);
        pen.Freeze();
        return pen;
    }

    private void DrawCenteredMessage(DrawingContext dc, string message, Rect bounds)
    {
        var text = new FormattedText(message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            SizeTypeface, 16, HintText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(Math.Max(10, (bounds.Width - text.Width) / 2), Math.Max(10, (bounds.Height - text.Height) / 2)));
    }

    private sealed record Tile(ScanNode? Node, long Size, bool Free = false);
    private sealed record HitRegion(Rect Bounds, Tile Tile);
}
