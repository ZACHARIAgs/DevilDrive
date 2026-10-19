using System;
using System.IO;

namespace CapstonePdm.Core.Vault;

public static class FileUtil
{
    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapstonePDM", "Backups");

    /// <summary>
    /// True if another process (SolidWorks) has the file open. SolidWorks keeps a handle on every
    /// open document, so an exclusive open fails while it's loaded.
    /// </summary>
    public static bool IsInUse(string fullPath)
    {
        if (!File.Exists(fullPath)) return false;
        try
        {
            using (new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                return false;
            }
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void SetReadOnly(string fullPath, bool readOnly)
    {
        if (!File.Exists(fullPath)) return;
        var attributes = File.GetAttributes(fullPath);
        var updated = readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
        if (updated != attributes) File.SetAttributes(fullPath, updated);
    }

    public static string NewBackupFolder() =>
        Path.Combine(BackupRoot, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));

    /// <summary>Copies a file into a backup folder, keeping its vault-relative folder structure.</summary>
    public static void Backup(string fullPath, string backupFolder, string relativePath)
    {
        if (!File.Exists(fullPath)) return;
        var destination = Path.Combine(backupFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(fullPath, destination, true);
        File.SetAttributes(destination, FileAttributes.Normal);
    }
}
