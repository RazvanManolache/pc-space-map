using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PcSpaceMap.Controls;

public sealed class ToolbarWrapPanel : WrapPanel
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arrangedSize = base.ArrangeOverride(finalSize);
        if (Orientation != Orientation.Horizontal || InternalChildren.Count == 0) return arrangedSize;

        Rect? right = null;
        if (InternalChildren[InternalChildren.Count - 1] is FrameworkElement { HorizontalAlignment: HorizontalAlignment.Right } lastGroup)
        {
            var slot = LayoutInformation.GetLayoutSlot(lastGroup);
            slot.X = Math.Max(slot.X, finalSize.Width - slot.Width);
            lastGroup.Arrange(slot);
            right = slot;
        }

        for (var first = 0; first < InternalChildren.Count; first++)
        {
            if (InternalChildren[first] is not FrameworkElement { HorizontalAlignment: HorizontalAlignment.Center } center) continue;
            var slot = LayoutInformation.GetLayoutSlot(center);
            var end = first + 1;
            var endX = slot.Right;
            while (end < InternalChildren.Count
                && InternalChildren[end] is FrameworkElement { HorizontalAlignment: HorizontalAlignment.Center } next)
            {
                var nextSlot = LayoutInformation.GetLayoutSlot(next);
                if (nextSlot.Y != slot.Y) break;
                endX = nextSlot.Right;
                end++;
            }

            // Center the group where space allows without crossing either neighbor.
            var width = endX - slot.Left;
            var boundary = right is { } rightSlot && rightSlot.Y == slot.Y ? rightSlot.Left : finalSize.Width;
            var x = Math.Clamp((finalSize.Width - width) / 2, slot.Left, Math.Max(slot.Left, boundary - width));
            for (var i = first; i < end; i++)
            {
                var item = (FrameworkElement)InternalChildren[i];
                var itemSlot = LayoutInformation.GetLayoutSlot(item);
                itemSlot.X += x - slot.Left;
                item.Arrange(itemSlot);
            }
            first = end - 1;
        }
        return arrangedSize;
    }
}
