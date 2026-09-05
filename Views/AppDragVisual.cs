using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FocusClip.Interop;
using FocusClip.Models;

namespace FocusClip.Views;

/// <summary>Non-activating, click-through icon carried by an OLE drag.</summary>
internal sealed class AppDragVisual : IDisposable
{
    private readonly Window _source;
    private readonly Window _ghost;

    public AppDragVisual(Window source, IReadOnlyList<AppEntry> items)
    {
        _source = source;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Image { Source = items[0].Icon, Width = 28, Height = 28 });
        content.Children.Add(new TextBlock
        {
            Text = items.Count > 1 ? $"{items[0].Name} 외 {items.Count - 1}개" : items[0].Name,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0), MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis
        });
        _ghost = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, IsHitTestVisible = false,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 35, 35, 42)),
                BorderBrush = (Brush)source.FindResource("AccentBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Child = content
            }
        };
        _ghost.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(_ghost).Handle;
            NativeMethods.MakeNoActivateToolWindow(handle);
            NativeMethods.SetClickThrough(handle, true);
            Move();
        };
        _source.GiveFeedback += Feedback;
        _ghost.Show();
        Move();
    }

    private void Move()
    {
        if (NativeMethods.GetCursorPos(out var p))
            NativeMethods.SetWindowPos(new WindowInteropHelper(_ghost).Handle, IntPtr.Zero,
                p.X + 12, p.Y + 12, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | 0x0004);
    }

    private void Feedback(object sender, GiveFeedbackEventArgs e)
    {
        Move();
        e.UseDefaultCursors = false;
        Mouse.SetCursor(e.Effects == DragDropEffects.None ? Cursors.No : Cursors.Arrow);
        e.Handled = true;
    }

    public void Dispose()
    {
        _source.GiveFeedback -= Feedback;
        _ghost.Close();
        Mouse.SetCursor(null);
    }
}
