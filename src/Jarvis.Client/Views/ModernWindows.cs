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
        if (sender is not Window window || window.Background is not SolidColorBrush background) return;
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var caption = ToColorRef(background.Color);
            var text = ToColorRef(Color.FromRgb(0x1F, 0x29, 0x37));
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
