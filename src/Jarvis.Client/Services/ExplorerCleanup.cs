using System;
using System.IO;
using Jarvis.Core.Config;
using Microsoft.Win32;

namespace Jarvis.Client.Services;

/// <summary>
/// Jarvis used to add a "Jarvis" submenu to File Explorer's right-click menu and a 3D preview handler for
/// parts and assemblies (up to 0.6.9). Both are gone; this removes what an older version registered, every
/// time Jarvis starts (it's quick, and does nothing once it's clean). Only Jarvis's own keys and values are
/// touched: a preview handler entry is removed only if it still points at Jarvis's handler, so SolidWorks'
/// or eDrawings' own previews are never affected.
/// </summary>
internal static class ExplorerCleanup
{
    private static readonly string[] MenuKeys =
    {
        @"Software\Classes\*\shell\Jarvis",
        @"Software\Classes\Directory\Background\shell\Jarvis",
        // Before Jarvis was renamed.
        @"Software\Classes\*\shell\CapstonePdm",
        @"Software\Classes\Directory\Background\shell\CapstonePdm",
    };

    private const string PreviewClsid = "{6E3F2B9A-1C4D-4E8B-9F27-5A0C3D81B7E4}";
    private const string PreviewHandlerShellEx = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private static readonly string[] PreviewExtensions = { ".sldprt", ".sldasm" };

    /// <summary>Removes the old menu and preview handler. Never throws: a leftover is retried next start.</summary>
    public static void RemoveOldIntegration()
    {
        var hkcu = Registry.CurrentUser;
        foreach (var key in MenuKeys) Try(() => hkcu.DeleteSubKeyTree(key, throwOnMissingSubKey: false));

        foreach (var extension in PreviewExtensions)
        {
            Try(() =>
            {
                var path = $@"Software\Classes\{extension}\ShellEx\{PreviewHandlerShellEx}";
                bool ours;
                using (var shellEx = hkcu.OpenSubKey(path))
                {
                    ours = shellEx != null && string.Equals(shellEx.GetValue(null) as string, PreviewClsid, StringComparison.OrdinalIgnoreCase);
                }
                if (ours) hkcu.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            });
        }
        Try(() => hkcu.DeleteSubKeyTree($@"Software\Classes\CLSID\{PreviewClsid}", throwOnMissingSubKey: false));
        Try(() =>
        {
            using var handlers = hkcu.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers", writable: true);
            handlers?.DeleteValue(PreviewClsid, throwOnMissingValue: false);
        });

        // The handler's files (in use while File Explorer's preview host has them loaded: then next time).
        Try(() =>
        {
            var folder = Path.Combine(AppFolders.InstallFolder, "PreviewHandler");
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        });
        Try(() =>
        {
            var list = Path.Combine(AppFolders.Roaming, "vaults.txt");
            if (File.Exists(list)) File.Delete(list);
        });
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            // Leave it; Jarvis tries again next time it starts.
        }
    }
}
