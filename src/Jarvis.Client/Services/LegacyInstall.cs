using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Jarvis.Core.Config;

namespace Jarvis.Client.Services;

/// <summary>
/// Jarvis used to be called Capstone PDM and was installed as
/// %LocalAppData%\Programs\CapstonePDM\CapstonePDM.exe. An update from that version replaces the old
/// exe in place; on its first start the new one copies itself to Programs\Jarvis\Jarvis.exe, points the
/// shortcuts there, restarts from there and removes the old folder.
/// </summary>
internal static class LegacyInstall
{
    public const string FinishArgument = "--finish-move";
    private const string ShortcutName = "Jarvis.lnk";
    private const string LegacyShortcutName = "Capstone PDM.lnk";

    public static string ExePath => Path.Combine(AppFolders.InstallFolder, AppFolders.Name + ".exe");

    /// <summary>Running as the old CapstonePDM.exe in its old install folder.</summary>
    internal static bool IsLegacyPath(string exe) =>
        string.Equals(Path.GetFileName(exe), AppFolders.LegacyName + ".exe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFileName(Path.GetDirectoryName(exe)), AppFolders.LegacyName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves an old install to the new folder and starts it from there. True means the new copy is
    /// starting and this process should exit; false means carry on here (nothing to move, or it failed
    /// and is tried again next start).
    /// </summary>
    public static bool MoveIfNeeded()
    {
        var exe = Environment.ProcessPath;
        if (exe == null || !IsLegacyPath(exe)) return false;
        try
        {
            Directory.CreateDirectory(AppFolders.InstallFolder);
            File.Copy(exe, ExePath, overwrite: true);
            UpdateShortcuts(ExePath);
            Process.Start(new ProcessStartInfo(ExePath, $"{FinishArgument} \"{Path.GetDirectoryName(exe)}\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception)
        {
            return false;
        }
    }

    /// <summary>After the move: waits for the old copy to exit and removes the old install folder.</summary>
    public static async Task FinishAsync(string oldFolder)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(oldFolder); attempt++)
        {
            if (RemoveLegacyFolder(oldFolder)) return;
            await Task.Delay(500);
        }
    }

    /// <summary>Removes what's left of the old install folder (File Explorer may still have its preview DLL loaded).</summary>
    public static bool RemoveLegacyFolder(string? folder = null)
    {
        folder ??= AppFolders.LegacyInstallFolder;
        // Only ever the old install folder, whatever the command line said.
        if (!string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), AppFolders.LegacyInstallFolder, StringComparison.OrdinalIgnoreCase)) return true;
        if (!Directory.Exists(folder)) return true;
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Desktop and Start menu: replaces "Capstone PDM" (and any old "Jarvis") shortcut with one to <paramref name="exe"/>.</summary>
    private static void UpdateShortcuts(string exe)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
        };
        foreach (var folder in folders)
        {
            try
            {
                var legacy = Path.Combine(folder, LegacyShortcutName);
                var shortcut = Path.Combine(folder, ShortcutName);
                if (!File.Exists(legacy) && !File.Exists(shortcut)) continue;
                File.Delete(legacy);
                CreateShortcut(shortcut, exe);
            }
            catch (Exception)
            {
                // A convenience: Setup makes them again if needed.
            }
        }
    }

    private static void CreateShortcut(string path, string exe)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell isn't available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = exe;
        link.IconLocation = exe + ",0";
        link.WorkingDirectory = Path.GetDirectoryName(exe);
        link.Description = "Check SolidWorks files in and out of your team's vault";
        link.Save();
    }
}
