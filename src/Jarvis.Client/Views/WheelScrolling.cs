using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Jarvis.Client.Views;

/// <summary>
/// Sideways scrolling everywhere in the app (WPF only scrolls up and down by itself): Shift + mouse wheel,
/// a tilting mouse wheel, and a two-finger swipe left or right on a touchpad. Scrolls whichever list or panel
/// is under the mouse, if it's wider than its window.
/// </summary>
internal static class WheelScrolling
{
    private const int WmMouseHWheel = 0x020E;
    /// <summary>Pixels per wheel notch (120 units), like a browser.</summary>
    private const double PixelsPerNotch = 48;

    public static void Apply()
    {
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    /// <summary>Hooks a window for tilt wheels and touchpad swipes (they arrive as a separate Windows message).</summary>
    public static void Attach(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source || Hooked.TryGetValue(source, out _)) return;
        Hooked.Add(source, new object()); // Loaded can fire more than once: hook each window once
        source.AddHook(Hook);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HwndSource, object> Hooked = new();

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.Shift || sender is not ScrollViewer viewer || viewer.ScrollableWidth <= 0) return;
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta / 120.0 * PixelsPerNotch);
        e.Handled = true;
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmMouseHWheel) return IntPtr.Zero;
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF); // positive = right
        if (Mouse.DirectlyOver is DependencyObject over && FindScrollable(over) is { } viewer)
        {
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + delta / 120.0 * PixelsPerNotch);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>The nearest scroll area around the mouse that can scroll sideways.</summary>
    private static ScrollViewer? FindScrollable(DependencyObject from)
    {
        for (var node = from; node != null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer viewer && viewer.ScrollableWidth > 0) return viewer;
        }
        return null;
    }
}
