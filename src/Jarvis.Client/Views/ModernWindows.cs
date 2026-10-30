using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Jarvis.Client.Services;

namespace Jarvis.Client.Views;

/// <summary>
/// Gives every window the app's look (Themes/Modern.xaml): the window style for all Window
/// subclasses, and on Windows 10/11 a title bar in the window's own color with dark mode support.
/// </summary>
internal static class ModernWindows
{
    private const int CaptionColorAttribute = 35; // DWMWA_CAPTION_COLOR (Windows 11)
    private const int TextColorAttribute = 36;    // DWMWA_TEXT_COLOR
    private const int ImmersiveDarkModeAttribute = 20; // DWMWA_USE_IMMERSIVE_DARK_MODE

    public static void Apply(Application app)
    {
        // Implicit styles only match the exact type, and every window here is a subclass of Window.
        FrameworkElement.StyleProperty.OverrideMetadata(typeof(Window),
            new FrameworkPropertyMetadata { DefaultValue = app.FindResource("ModernWindow") });
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        WheelScrolling.Apply();

        ThemeManager.ThemeChanged += isDark =>
        {
            foreach (Window window in app.Windows)
            {
                UpdateTitleBar(window, isDark);
            }
        };
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window hooked)
        {
            WheelScrolling.Attach(hooked);
            UpdateTitleBar(hooked, ThemeManager.IsDark);
        }
    }

    public static void UpdateTitleBar(Window window, bool isDark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int darkVal = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, ImmersiveDarkModeAttribute, ref darkVal, sizeof(int));

            var bg = isDark ? Color.FromRgb(0x18, 0x18, 0x1B) : Color.FromRgb(0xF3, 0xF4, 0xF6);
            var fg = isDark ? Color.FromRgb(0xF4, 0xF4, 0xF5) : Color.FromRgb(0x1F, 0x29, 0x37);

            var caption = ToColorRef(bg);
            var text = ToColorRef(fg);
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
