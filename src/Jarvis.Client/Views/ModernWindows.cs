using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Jarvis.Client.Views;

/// <summary>
/// Gives every window the app's look (Themes/Modern.xaml): the window style for all Window
/// subclasses, and on Windows 11 a title bar in the window's own color so it blends with the
/// command bar instead of standing out as a separate strip.
/// </summary>
internal static class ModernWindows
{
    private const int DarkModeAttribute = 20;     // DWMWA_USE_IMMERSIVE_DARK_MODE (the title bar's buttons)
    private const int CaptionColorAttribute = 35; // DWMWA_CAPTION_COLOR (Windows 11)
    private const int TextColorAttribute = 36;    // DWMWA_TEXT_COLOR

    public static void Apply(Application app)
    {
        // Implicit styles only match the exact type, and every window here is a subclass of Window.
        FrameworkElement.StyleProperty.OverrideMetadata(typeof(Window),
            new FrameworkPropertyMetadata { DefaultValue = app.FindResource("ModernWindow") });
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        WheelScrolling.Apply();
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window hooked) WheelScrolling.Attach(hooked);
        if (sender is Window window) StyleTitleBar(window);
    }

    /// <summary>Colors the window's title bar like the window itself (again whenever the look switches between light and dark).</summary>
    public static void StyleTitleBar(Window window)
    {
        if (window.Background is not SolidColorBrush background) return;
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var dark = ThemeManager.IsDark ? 1 : 0;
            var caption = ToColorRef(background.Color);
            var text = ToColorRef(window.TryFindResource("TextBrush") is SolidColorBrush textBrush ? textBrush.Color : Color.FromRgb(0x1F, 0x29, 0x37));
            DwmSetWindowAttribute(hwnd, DarkModeAttribute, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, CaptionColorAttribute, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, TextColorAttribute, ref text, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
        {
            // Older Windows: keep the standard title bar.
        }
    }

    /// <summary>Win32 COLORREF: 0x00BBGGRR.</summary>
    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
