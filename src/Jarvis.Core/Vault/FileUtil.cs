using System;
using System.IO;

namespace Jarvis.Core.Vault;

public static class FileUtil
{
    public static string BackupRoot => Path.Combine(
        Config.AppFolders.Local, "Backups");

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

    /// <summary>
    /// True if the file can't be read at all (another program has it open exclusively, or mid-write).
    /// Unlike <see cref="IsInUse"/>, a file SolidWorks merely has loaded is still readable: SolidWorks
    /// shares its handle, so Git can check the saved file in while an assembly using it stays open.
    /// </summary>
    public static bool IsUnreadable(string fullPath)
    {
        if (!File.Exists(fullPath)) return false;
        try
        {
            using (new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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

    /// <summary>True if both files exist and have exactly the same bytes (false if either can't be read).</summary>
    public static bool SameContent(string a, string b)
    {
        try
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (!infoA.Exists || !infoB.Exists || infoA.Length != infoB.Length) return false;
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var streamA = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var streamB = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToBase64String(sha.ComputeHash(streamA)) == Convert.ToBase64String(sha.ComputeHash(streamB));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
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

    /// <summary>How long backups are kept before <see cref="PruneBackups"/> removes them.</summary>
    public static readonly TimeSpan BackupsKeptFor = TimeSpan.FromDays(60);

    /// <summary>
    /// Deletes backup folders older than <paramref name="olderThan"/> (they're named by date and time, see
    /// <see cref="NewBackupFolder"/>). Returns how many were removed. Anything else in the folder is left alone.
    /// </summary>
    public static int PruneBackups(TimeSpan olderThan, string? root = null, DateTime? now = null)
    {
        root ??= BackupRoot;
        var removed = 0;
        try
        {
            if (!Directory.Exists(root)) return 0;
            var cutoff = (now ?? DateTime.Now) - olderThan;
            foreach (var folder in Directory.GetDirectories(root))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(folder), "yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var made) || made >= cutoff) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(folder, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // In use right now: it goes next time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // The backups folder can't be read: nothing to clean.
        }
        return removed;
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
