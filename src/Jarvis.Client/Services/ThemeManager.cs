using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Jarvis.Client.Services;

/// <summary>
/// Manages application themes (Light and Dark mode) with the accent color locked to #FFC627 (Solar Devils / ASU Gold).
/// Updates dynamic WPF brushes and native Windows immersive dark mode window title bars.
/// </summary>
public static class ThemeManager
{
    public const string AccentHex = "#FFC627";
    public const string AccentHoverHex = "#E5B219";
    public const string AccentPressedHex = "#CC9E12";
    public const string AccentForegroundHex = "#18181B";

    public static bool IsDark { get; private set; }

    public static event Action<bool>? ThemeChanged;

    public static void ApplyTheme(bool isDark)
    {
        IsDark = isDark;
        var app = Application.Current;
        if (app == null) return;

        var res = app.Resources;

        // Accent palette (LOCKED to #FFC627)
        var accent = (Color)ColorConverter.ConvertFromString(AccentHex);
        var accentHover = (Color)ColorConverter.ConvertFromString(AccentHoverHex);
        var accentPressed = (Color)ColorConverter.ConvertFromString(AccentPressedHex);
        var accentFore = (Color)ColorConverter.ConvertFromString(AccentForegroundHex);

        res["AccentColor"] = accent;
        res["AccentBrush"] = MakeFrozenBrush(accent);
        res["AccentHoverBrush"] = MakeFrozenBrush(accentHover);
        res["AccentPressedBrush"] = MakeFrozenBrush(accentPressed);
        res["AccentForegroundBrush"] = MakeFrozenBrush(accentFore);

        if (isDark)
        {
            // Dark Mode Palette (Zinc-based dark theme)
            res["WindowBrush"] = MakeFrozenBrush("#18181B");
            res["SurfaceBrush"] = MakeFrozenBrush("#27272A");
            res["SubtleSurfaceBrush"] = MakeFrozenBrush("#202023");
            res["BorderBrush"] = MakeFrozenBrush("#3F3F46");
            res["StrongBorderBrush"] = MakeFrozenBrush("#52525B");
            res["DividerBrush"] = MakeFrozenBrush("#333338");
            res["TextBrush"] = MakeFrozenBrush("#F4F4F5");
            res["MutedTextBrush"] = MakeFrozenBrush("#A1A1AA");
            res["FaintTextBrush"] = MakeFrozenBrush("#71717A");
            res["HoverBrush"] = MakeFrozenBrush("#3F3F46");
            res["PressedBrush"] = MakeFrozenBrush("#52525B");
            res["RowHoverBrush"] = MakeFrozenBrush("#2E2E33");
            res["ToolGlyphBrush"] = MakeFrozenBrush("#E4E4E7");
            res["AccentSoftBrush"] = MakeFrozenBrush("#362A04");
            res["SelectionBrush"] = MakeFrozenBrush("#4A3B06");
        }
        else
        {
            // Light Mode Palette
            res["WindowBrush"] = MakeFrozenBrush("#F3F4F6");
            res["SurfaceBrush"] = MakeFrozenBrush("#FFFFFF");
            res["SubtleSurfaceBrush"] = MakeFrozenBrush("#F8FAFC");
            res["BorderBrush"] = MakeFrozenBrush("#E2E5EA");
            res["StrongBorderBrush"] = MakeFrozenBrush("#C9CFD7");
            res["DividerBrush"] = MakeFrozenBrush("#EEF0F3");
            res["TextBrush"] = MakeFrozenBrush("#1F2937");
            res["MutedTextBrush"] = MakeFrozenBrush("#6B7280");
            res["FaintTextBrush"] = MakeFrozenBrush("#9CA3AF");
            res["HoverBrush"] = MakeFrozenBrush("#E9ECF1");
            res["PressedBrush"] = MakeFrozenBrush("#DDE2E8");
            res["RowHoverBrush"] = MakeFrozenBrush("#F3F6FA");
            res["ToolGlyphBrush"] = MakeFrozenBrush("#374151");
            res["AccentSoftBrush"] = MakeFrozenBrush("#FFF8DC");
            res["SelectionBrush"] = MakeFrozenBrush("#FFE599");
        }

        // Apply native Windows 10/11 dark title bar to all active windows
        foreach (Window window in app.Windows)
        {
            SetWindowDarkMode(window, isDark);
        }

        ThemeChanged?.Invoke(isDark);
    }

    public static void SetWindowDarkMode(Window window, bool isDark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                window.SourceInitialized += (_, _) =>
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    if (handle != IntPtr.Zero) SetImmersiveDarkMode(handle, isDark);
                };
                return;
            }
            SetImmersiveDarkMode(hwnd, isDark);
        }
        catch
        {
            // Ignore on older Windows versions
        }
    }

    private static SolidColorBrush MakeFrozenBrush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        return MakeFrozenBrush(color);
    }

    private static SolidColorBrush MakeFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private static void SetImmersiveDarkMode(IntPtr hwnd, bool isDark)
    {
        int value = isDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
