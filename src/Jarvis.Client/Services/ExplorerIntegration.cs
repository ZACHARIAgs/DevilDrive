using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Jarvis.Client.Services;

/// <summary>
/// Adds a "Jarvis" submenu to File Explorer's right-click menu for files inside the vault, and
/// "Get Latest" for the background of vault folders. Per-user registry keys (no admin rights needed);
/// AppliesTo limits them to the vault folder. On Windows 11 they appear under "Show more options".
/// </summary>
internal static class ExplorerIntegration
{
    private const string FileMenuKey = @"Software\Classes\*\shell\Jarvis";
    private const string FolderMenuKey = @"Software\Classes\Directory\Background\shell\Jarvis";

    /// <summary>Adds the menus for files and folders inside any of the vaults (one per group).</summary>
    public static void Register(string exePath, IReadOnlyCollection<string> vaultPaths)
    {
        var vaults = vaultPaths.Select(v => v.TrimEnd('\\')).Where(v => v.Length > 0).ToList();
        if (vaults.Count == 0)
        {
            Unregister();
            return;
        }
        var icon = $"\"{exePath}\",0";
        var insideFiles = string.Join(" OR ", vaults.Select(v => $"System.ItemPathDisplay:~<\"{v}\\\""));
        var insideFolders = string.Join(" OR ", vaults.Select(v => $"System.ItemPathDisplay:~<\"{v}\""));

        Unregister();

        using (var menu = Registry.CurrentUser.CreateSubKey(FileMenuKey))
        {
            menu.SetValue("MUIVerb", "Jarvis");
            menu.SetValue("Icon", icon);
            menu.SetValue("SubCommands", "");
            menu.SetValue("AppliesTo", insideFiles);
        }
        AddCommand(FileMenuKey, "1checkout", "Check Out", exePath, "checkout", "\"%1\"");
        AddCommand(FileMenuKey, "2checkin", "Check In…", exePath, "checkin", "\"%1\"");
        AddCommand(FileMenuKey, "3undo", "Undo Check Out", exePath, "undo", "\"%1\"");
        AddCommand(FileMenuKey, "4download", "Download", exePath, "download", "\"%1\"");
        AddCommand(FileMenuKey, "5show", "Show in Jarvis", exePath, "show", "\"%1\"");

        using (var menu = Registry.CurrentUser.CreateSubKey(FolderMenuKey))
        {
            menu.SetValue("MUIVerb", "Jarvis");
            menu.SetValue("Icon", icon);
            menu.SetValue("SubCommands", "");
            menu.SetValue("AppliesTo", insideFolders);
        }
        AddCommand(FolderMenuKey, "1getlatest", "Get Latest", exePath, "getlatest", "\"%V\"");
        AddCommand(FolderMenuKey, "2open", "Open Jarvis", exePath, "open", "\"%V\"");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(FileMenuKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(FolderMenuKey, false);
    }

    /// <summary>Only an installed copy registers itself, not a development build.</summary>
    public static bool IsInstalledCopy(string exePath) =>
        exePath.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0;

    private static void AddCommand(string menuKey, string keyName, string label, string exePath, string command, string argument)
    {
        using var item = Registry.CurrentUser.CreateSubKey($@"{menuKey}\shell\{keyName}");
        item.SetValue("MUIVerb", label);
        using var commandKey = item.CreateSubKey("command");
        commandKey.SetValue(null, $"\"{exePath}\" --explorer {command} {argument}");
    }
}
