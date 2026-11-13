using System;
using System.Windows;
using Jarvis.Client.ViewModels;
using Microsoft.Win32;

namespace Jarvis.Client.Views;

/// <summary>
/// Light or dark. The colors are two resource dictionaries with the same names (Themes/Light.xaml, Themes/Dark.xaml);
/// the styles refer to them with DynamicResource, so swapping the dictionary repaints every open window.
/// The setting is "System" (follow Windows' app mode, also while Jarvis runs), "Light" or "Dark".
/// </summary>
internal static class ThemeManager
{
    public const string FollowWindows = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    private static string _setting = Dark;
    private static bool _watching;
    private static bool _initialized;

    public static bool IsDark { get; private set; }

    /// <summary>The look changed: anything that picked colors in code works them out again.</summary>
    public static event Action? Changed;

    public static string Normalize(string? setting) =>
        string.Equals(setting, FollowWindows, StringComparison.OrdinalIgnoreCase) ? FollowWindows :
        string.Equals(setting, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    public static void Apply(string? setting)
    {
        _setting = Normalize(setting);
        if (!_watching)
        {
            _watching = true;
            // Windows tells every app when its light/dark setting changes.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _setting == FollowWindows) Application.Current?.Dispatcher.BeginInvoke(new Action(() => Use(WindowsUsesDark())));
            };
        }
        Use(_setting == Dark || (_setting == FollowWindows && WindowsUsesDark()));
    }

    private static void Use(bool dark)
    {
        var app = Application.Current;
        if (app == null || (_initialized && dark == IsDark)) return;
        _initialized = true;
        // The palette is the first merged dictionary (see App.xaml).
        app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        IsDark = dark;
        FileRowViewModel.DarkMode = dark;
        foreach (Window window in app.Windows) ModernWindows.StyleTitleBar(window);
        Changed?.Invoke();
    }

    /// <summary>Windows' "Choose your app mode" is Dark (Settings, Personalization, Colors).</summary>
    private static bool WindowsUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
