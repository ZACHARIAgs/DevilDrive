using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Jarvis.Client.Services;

/// <summary>
/// Manages application themes (Light and Dark mode) following the Windows default or classic blue (#2563EB) accent.
/// Updates dynamic WPF brushes and native Windows immersive dark mode window title bars.
/// </summary>
public static class ThemeManager
{
    public const string DefaultAccentHex = "#2563EB";

    public static bool IsDark { get; private set; }

    public static event Action<bool>? ThemeChanged;

    public static void ApplyTheme(bool isDark)
    {
        IsDark = isDark;
        var app = Application.Current;
        if (app == null) return;

        var res = app.Resources;

        // Accent palette: follow Windows system accent or default blue (#2563EB)
        var accent = GetSystemAccentColor();
        var accentHover = Darken(accent, 0.85f);
        var accentPressed = Darken(accent, 0.72f);
        var lum = 0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B;
        var accentFore = lum > 145 ? Color.FromRgb(0x18, 0x18, 0x1B) : Colors.White;

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
            res["AccentSoftBrush"] = MakeFrozenBrush("#1E293B");
            res["SelectionBrush"] = MakeFrozenBrush("#1E3A5F");
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
            res["AccentSoftBrush"] = MakeFrozenBrush("#E8F0FE");
            res["SelectionBrush"] = MakeFrozenBrush("#DCE8FC");
        }

        // Apply native Windows 10/11 dark title bar to all active windows
        foreach (Window window in app.Windows)
        {
            SetWindowDarkMode(window, isDark);
        }

        ThemeChanged?.Invoke(isDark);
    }

    public static Color GetSystemAccentColor()
    {
        try
        {
            var dword = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
            if (dword is int val && val != 0)
            {
                byte r = (byte)(val & 0xFF);
                byte g = (byte)((val >> 8) & 0xFF);
                byte b = (byte)((val >> 16) & 0xFF);
                var lum = 0.299 * r + 0.587 * g + 0.114 * b;
                // Only use if reasonably visible (not pitch black or blindingly white)
                if (lum > 40 && lum < 225)
                {
                    return Color.FromRgb(r, g, b);
                }
            }
        }
        catch
        {
            // Registry read failed, fall back to default blue
        }
        return (Color)ColorConverter.ConvertFromString(DefaultAccentHex);
    }

    private static Color Darken(Color c, float factor) =>
        Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

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
