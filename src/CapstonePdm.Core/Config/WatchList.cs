using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CapstonePdm.Core.Vault;

namespace CapstonePdm.Core.Config;

/// <summary>
/// "Notify me when it's free": files someone else has checked out that this user is waiting for.
/// Kept in %AppData%\CapstonePDM\watching.json so the desktop app and the SolidWorks add-in share it;
/// whichever sees the file become free first tells the user and removes it.
/// </summary>
public static class WatchList
{
    private sealed class Entry
    {
        public string Vault { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTimeOffset Since { get; set; }
    }

    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CapstonePDM", "watching.json");

    public static bool IsWatching(string vaultRoot, string relativePath) =>
        WithList(list => list.Any(e => Same(e, vaultRoot, relativePath)), save: false);

    public static void Watch(string vaultRoot, string relativePath) => WithList(list =>
    {
        if (!list.Any(e => Same(e, vaultRoot, relativePath)))
        {
            list.Add(new Entry { Vault = vaultRoot, Path = relativePath, Since = DateTimeOffset.Now });
        }
        return true;
    });

    public static void Stop(string vaultRoot, string relativePath) =>
        WithList(list => list.RemoveAll(e => Same(e, vaultRoot, relativePath)) > 0);

    /// <summary>
    /// Watched files in this vault that nobody else has checked out any more. They're removed from the
    /// list, so the user is told once. Does nothing when check-outs are unknown (offline).
    /// </summary>
    public static List<VaultFile> TakeNowFree(string vaultRoot, VaultSnapshot snapshot)
    {
        if (snapshot.IsOffline || snapshot.LocksUnknown) return new List<VaultFile>();
        var files = snapshot.Files.ToDictionary(f => f.RelativePath, f => f, StringComparer.OrdinalIgnoreCase);
        var free = new List<VaultFile>();
        WithList(list =>
        {
            foreach (var entry in list.Where(e => string.Equals(e.Vault, vaultRoot, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (!files.TryGetValue(entry.Path, out var file))
                {
                    list.Remove(entry); // deleted or renamed: stop watching
                }
                else if (file.State != FileState.CheckedOutByOther && file.State != FileState.Frozen)
                {
                    free.Add(file);
                    list.Remove(entry);
                }
            }
            return free.Count > 0;
        });
        return free;
    }

    private static bool Same(Entry e, string vaultRoot, string relativePath) =>
        string.Equals(e.Vault, vaultRoot, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(e.Path, relativePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the list, runs the change, and saves if it changed, under a lock shared with the add-in.</summary>
    private static T WithList<T>(Func<List<Entry>, T> change, bool save = true)
    {
        using var mutex = new Mutex(false, "CapstonePDM.WatchList");
        var owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            var list = new List<Entry>();
            try
            {
                if (File.Exists(FilePath)) list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath), Json.Options) ?? list;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                // Unreadable list: start fresh.
            }

            var result = change(list);
            if (save && result is bool changed && changed)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json.Options));
            }
            return result;
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }
}
