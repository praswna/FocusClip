using System;
using System.Windows;
using System.Windows.Media;

namespace FocusClip.Views;

internal static class PopupSizing
{
    // Re-measure the scroll viewport and its children without the collapsed
    // window's height constraint before asking WPF to resize the native window.
    public static void Refresh(Window window)
    {
        if (window.Content is not FrameworkElement root) return;
        Invalidate(root);
        root.Measure(new Size(window.Width, double.PositiveInfinity));
        window.SizeToContent = SizeToContent.Manual;
        window.Height = Math.Max(window.MinHeight, root.DesiredSize.Height);
        window.SizeToContent = SizeToContent.Height;
        window.InvalidateMeasure();
        window.UpdateLayout();
    }

    private static void Invalidate(UIElement element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (VisualTreeHelper.GetChild(element, i) is UIElement child)
                Invalidate(child);
        element.InvalidateMeasure();
    }
}
