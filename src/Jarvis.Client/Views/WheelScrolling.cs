using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Jarvis.Client.Views;

/// <summary>
/// Scrolling everywhere in the app. Up and down: by how far the wheel or touchpad actually moved (WPF on its
/// own jumps a fixed 48 pixels per event, so a touchpad's many small events made lists race). Sideways (WPF
/// can't on its own): Shift + mouse wheel, a tilting mouse wheel, and a two-finger swipe left or right on a
/// touchpad. Always the innermost list or panel under the mouse; the 3D preview keeps the wheel for zooming.
/// </summary>
internal static class WheelScrolling
{
    private const int WmMouseHWheel = 0x020E;
    /// <summary>Pixels per wheel notch (120 units) sideways.</summary>
    private const double PixelsPerNotch = 48;
    /// <summary>Pixels per wheel notch up and down: a little over one row of the file list. Touchpads scroll in proportion.</summary>
    private const double VerticalPixelsPerNotch = 40;

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
        if (e.Handled || sender is not ScrollViewer viewer || e.OriginalSource is not DependencyObject source || IsIn3DView(source)) return;
        // This runs outermost first: only the innermost list or panel that can scroll that way takes it.
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            if (!ReferenceEquals(FindScrollable(source, v => v.ScrollableWidth > 0), viewer)) return;
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta / 120.0 * PixelsPerNotch);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None)
        {
            if (!ReferenceEquals(FindScrollable(source, v => v.ScrollableHeight > 0), viewer)) return;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 120.0 * VerticalPixelsPerNotch);
            e.Handled = true;
        }
    }

    /// <summary>The wheel zooms the 3D preview; leave it alone there.</summary>
    private static bool IsIn3DView(DependencyObject from)
    {
        for (var node = from; node != null; node = Parent(node))
        {
            if (node is HelixToolkit.Wpf.HelixViewport3D) return true;
        }
        return false;
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmMouseHWheel) return IntPtr.Zero;
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF); // positive = right
        if (Mouse.DirectlyOver is DependencyObject over && FindScrollable(over, v => v.ScrollableWidth > 0) is { } viewer)
        {
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + delta / 120.0 * PixelsPerNotch);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>The nearest scroll area around the mouse that can scroll that way.</summary>
    private static ScrollViewer? FindScrollable(DependencyObject from, Func<ScrollViewer, bool> canScroll)
    {
        for (var node = from; node != null; node = Parent(node))
        {
            if (node is ScrollViewer viewer && canScroll(viewer)) return viewer;
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
