using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PcSpaceMap.Models;

namespace PcSpaceMap.Controls;

public sealed class TreemapControl : FrameworkElement
{
    private readonly List<HitRegion> _hitRegions = [];
    private ScanNode? _root;
    private ScanNode? _hovered;

    public ScanNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            InvalidateVisual();
        }
    }

    public event EventHandler<ScanNode>? NodeSelected;
    public event EventHandler<ScanNode>? ZoomRequested;

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
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 16, 29)), null, bounds);

        if (_root is null || bounds.Width < 20 || bounds.Height < 20)
        {
            DrawCenteredMessage(drawingContext, "Choose a folder or drive, then start a scan.", bounds);
            return;
        }

        if (_root.Size <= 0 || _root.Children.All(x => x.Size <= 0))
        {
            DrawCenteredMessage(drawingContext, "No sized files were found in this location.", bounds);
            return;
        }

        var items = _root.Children.Where(x => x.Size > 0).OrderByDescending(x => x.Size).Take(350).ToList();
        LayoutGroup(drawingContext, items, new Rect(2, 2, Math.Max(0, bounds.Width - 4), Math.Max(0, bounds.Height - 4)), 0);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTestNode(e.GetPosition(this));
        if (ReferenceEquals(hit, _hovered)) return;
        _hovered = hit;
        Cursor = hit is null ? Cursors.Arrow : Cursors.Hand;
        ToolTip = hit is null ? null : $"{hit.Path}\n{hit.SizeText} · {hit.ContentsText}\nDouble-click a folder to zoom in";
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var hit = HitTestNode(e.GetPosition(this));
        if (hit is null) return;
        NodeSelected?.Invoke(this, hit);
        if (e.ClickCount >= 2 && hit.IsDirectory)
            ZoomRequested?.Invoke(this, hit);
    }

    private void LayoutGroup(DrawingContext dc, IReadOnlyList<ScanNode> items, Rect area, int depth)
    {
        if (items.Count == 0 || area.Width < 2 || area.Height < 2) return;
        if (items.Count == 1)
        {
            DrawNode(dc, items[0], area, depth);
            return;
        }

        var total = items.Sum(x => (double)x.Size);
        if (total <= 0) return;

        var target = total / 2;
        double firstTotal = 0;
        var split = 0;
        while (split < items.Count - 1 && firstTotal + items[split].Size <= target)
        {
            firstTotal += items[split].Size;
            split++;
        }
        if (split == 0)
        {
            split = 1;
            firstTotal = items[0].Size;
        }

        var ratio = Math.Clamp(firstTotal / total, 0.03, 0.97);
        Rect firstArea, secondArea;
        if (area.Width >= area.Height)
        {
            var width = area.Width * ratio;
            firstArea = new Rect(area.X, area.Y, width, area.Height);
            secondArea = new Rect(area.X + width, area.Y, area.Width - width, area.Height);
        }
        else
        {
            var height = area.Height * ratio;
            firstArea = new Rect(area.X, area.Y, area.Width, height);
            secondArea = new Rect(area.X, area.Y + height, area.Width, area.Height - height);
        }

        LayoutGroup(dc, items.Take(split).ToList(), Deflate(firstArea, 0.6), depth);
        LayoutGroup(dc, items.Skip(split).ToList(), Deflate(secondArea, 0.6), depth);
    }

    private void DrawNode(DrawingContext dc, ScanNode node, Rect area, int depth)
    {
        if (area.Width < 1 || area.Height < 1) return;
        _hitRegions.Add(new HitRegion(area, node, depth));

        var baseColor = ColorFor(node, depth);
        var fill = new LinearGradientBrush(
            ChangeBrightness(baseColor, 0.10),
            ChangeBrightness(baseColor, -0.13),
            new Point(0, 0), new Point(1, 1));
        var border = new Pen(new SolidColorBrush(ChangeBrightness(baseColor, 0.32)), depth == 0 ? 1.2 : 0.7);
        dc.DrawRoundedRectangle(fill, border, area, Math.Min(5, area.Width / 8), Math.Min(5, area.Height / 8));

        var headerHeight = area.Height >= 34 && area.Width >= 70 ? Math.Min(28, area.Height * 0.3) : 0;
        if (headerHeight > 0)
        {
            dc.PushClip(new RectangleGeometry(area));
            var fontSize = area.Width > 220 && area.Height > 80 ? 13 : 11;
            var title = new FormattedText(
                node.Name,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                fontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, area.Width - 12),
                MaxTextHeight = headerHeight,
                Trimming = TextTrimming.CharacterEllipsis
            };
            dc.DrawText(title, new Point(area.X + 6, area.Y + 4));

            if (area.Width > 120 && area.Height > 58)
            {
                var sizeText = new FormattedText(
                    node.SizeText,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    10,
                    new SolidColorBrush(Color.FromArgb(215, 235, 244, 255)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(sizeText, new Point(area.X + 6, area.Bottom - 20));
            }
            dc.Pop();
        }

        if (node.IsDirectory && depth < 3 && area.Width > 105 && area.Height > 75 && node.Children.Count > 0)
        {
            var inner = new Rect(area.X + 3, area.Y + Math.Max(24, headerHeight), Math.Max(0, area.Width - 6), Math.Max(0, area.Height - Math.Max(27, headerHeight) - 3));
            var children = node.Children.Where(x => x.Size > 0).OrderByDescending(x => x.Size).Take(80).ToList();
            LayoutGroup(dc, children, inner, depth + 1);
        }
    }

    private ScanNode? HitTestNode(Point point)
    {
        for (var i = _hitRegions.Count - 1; i >= 0; i--)
        {
            if (_hitRegions[i].Bounds.Contains(point)) return _hitRegions[i].Node;
        }
        return null;
    }

    private static Rect Deflate(Rect rect, double amount) => new(
        rect.X + amount,
        rect.Y + amount,
        Math.Max(0, rect.Width - amount * 2),
        Math.Max(0, rect.Height - amount * 2));

    private static Color ColorFor(ScanNode node, int depth)
    {
        var key = node.IsDirectory ? node.Name : System.IO.Path.GetExtension(node.Name);
        var hash = 17;
        foreach (var character in key)
            hash = unchecked(hash * 31 + character);
        var hue = Math.Abs(hash % 360);
        var saturation = node.IsDirectory ? 0.58 : 0.68;
        var lightness = 0.33 + Math.Min(depth, 3) * 0.025;
        return HslToRgb(hue, saturation, lightness);
    }

    private static Color HslToRgb(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        var m = lightness - chroma / 2;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static Color ChangeBrightness(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R + amount * 255, 0, 255),
        (byte)Math.Clamp(color.G + amount * 255, 0, 255),
        (byte)Math.Clamp(color.B + amount * 255, 0, 255));

    private void DrawCenteredMessage(DrawingContext dc, string message, Rect bounds)
    {
        var text = new FormattedText(message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 16, new SolidColorBrush(Color.FromRgb(145, 164, 190)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(Math.Max(10, (bounds.Width - text.Width) / 2), Math.Max(10, (bounds.Height - text.Height) / 2)));
    }

    private sealed record HitRegion(Rect Bounds, ScanNode Node, int Depth);
}
