using System;
using System.IO;
using Microsoft.Win32;

namespace CapstonePdm.Client.Services;

/// <summary>
/// Adds a "Capstone PDM" submenu to File Explorer's right-click menu for files inside the vault, and
/// "Get Latest" for the background of vault folders. Per-user registry keys (no admin rights needed);
/// AppliesTo limits them to the vault folder. On Windows 11 they appear under "Show more options".
/// </summary>
internal static class ExplorerIntegration
{
    private const string FileMenuKey = @"Software\Classes\*\shell\CapstonePDM";
    private const string FolderMenuKey = @"Software\Classes\Directory\Background\shell\CapstonePDM";

    public static void Register(string exePath, string vaultPath)
    {
        var vault = vaultPath.TrimEnd('\\');
        var icon = $"\"{exePath}\",0";

        Unregister();

        using (var menu = Registry.CurrentUser.CreateSubKey(FileMenuKey))
        {
            menu.SetValue("MUIVerb", "Capstone PDM");
            menu.SetValue("Icon", icon);
            menu.SetValue("SubCommands", "");
            menu.SetValue("AppliesTo", $"System.ItemPathDisplay:~<\"{vault}\\\"");
        }
        AddCommand(FileMenuKey, "1checkout", "Check Out", exePath, "checkout", "\"%1\"");
        AddCommand(FileMenuKey, "2checkin", "Check In…", exePath, "checkin", "\"%1\"");
        AddCommand(FileMenuKey, "3undo", "Undo Check Out", exePath, "undo", "\"%1\"");
        AddCommand(FileMenuKey, "4download", "Download", exePath, "download", "\"%1\"");
        AddCommand(FileMenuKey, "5show", "Show in Capstone PDM", exePath, "show", "\"%1\"");

        using (var menu = Registry.CurrentUser.CreateSubKey(FolderMenuKey))
        {
            menu.SetValue("MUIVerb", "Capstone PDM");
            menu.SetValue("Icon", icon);
            menu.SetValue("SubCommands", "");
            menu.SetValue("AppliesTo", $"System.ItemPathDisplay:~<\"{vault}\"");
        }
        AddCommand(FolderMenuKey, "1getlatest", "Get Latest", exePath, "getlatest", "\"%V\"");
        AddCommand(FolderMenuKey, "2open", "Open Capstone PDM", exePath, "open", "\"%V\"");
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
