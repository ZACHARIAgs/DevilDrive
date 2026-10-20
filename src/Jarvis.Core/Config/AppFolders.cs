using System;
using System.Collections.Generic;
using System.IO;

namespace Jarvis.Core.Config;

/// <summary>
/// Where Jarvis keeps its per-user files: %AppData%\Jarvis (settings, shared by the app and the
/// add-in) and %LocalAppData%\Jarvis (backups, logs). Jarvis used to be called Capstone PDM; the
/// first time a folder is used, files from the old "CapstonePDM" folder are moved over.
/// </summary>
public static class AppFolders
{
    public const string Name = "Jarvis";
    /// <summary>Folder and file name from before the rename.</summary>
    public const string LegacyName = "CapstonePDM";

    private static readonly object Gate = new object();
    private static readonly HashSet<string> Checked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>%AppData%\Jarvis</summary>
    public static string Roaming => Folder(Environment.SpecialFolder.ApplicationData);

    /// <summary>%LocalAppData%\Jarvis</summary>
    public static string Local => Folder(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>%LocalAppData%\Programs\Jarvis: where Setup installs the app.</summary>
    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);

    /// <summary>%LocalAppData%\Programs\CapstonePDM: the install folder before the rename.</summary>
    public static string LegacyInstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", LegacyName);

    /// <summary>
    /// The SolidWorks add-in from before the rename is still installed (its update moves it). It reads
    /// the old folders, so they stay until then.
    /// </summary>
    private static bool LegacyAddinInstalled =>
        Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), LegacyName, "SolidWorksAddin"));

    private static string Folder(Environment.SpecialFolder root)
    {
        var parent = Environment.GetFolderPath(root);
        var folder = Path.Combine(parent, Name);
        lock (Gate)
        {
            if (Checked.Add(folder)) MoveLegacy(Path.Combine(parent, LegacyName), folder, keepLegacy: LegacyAddinInstalled);
        }
        return folder;
    }

    /// <summary>
    /// Copies files from <paramref name="legacy"/> into <paramref name="folder"/>, keeping any file that
    /// already exists there, then removes the old folder (unless <paramref name="keepLegacy"/>). Never
    /// throws: a file that's in use stays behind.
    /// </summary>
    internal static void MoveLegacy(string legacy, string folder, bool keepLegacy = false)
    {
        try
        {
            if (!Directory.Exists(legacy)) return;
            foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var target = Path.Combine(folder, file.Substring(legacy.Length).TrimStart(Path.DirectorySeparatorChar));
                    if (File.Exists(target)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // In use (e.g. a log the old add-in has open); skip it.
                }
            }
            if (!keepLegacy) Directory.Delete(legacy, recursive: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Something is still in use; the copies are in place, the old folder goes another time.
        }
    }
}
