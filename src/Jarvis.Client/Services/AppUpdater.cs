using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Jarvis.Core.Errors;
using Jarvis.Core.Install;
using Jarvis.Core.Updates;

namespace Jarvis.Client.Services;

/// <summary>
/// Checks GitHub Releases at startup. When there's a newer version it asks, downloads the exe (and the
/// SolidWorks add-in if it's installed), checks both against the release's SHA256SUMS, swaps the exe
/// and restarts. Only the copy in the install folder updates itself, not a development build or a download.
/// </summary>
public static class AppUpdater
{
    /// <summary>Removes the previous exe left behind by the last update.</summary>
    public static void CleanUpAfterUpdate()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch (Exception)
        {
            // Still in use for a moment after restarting; it's removed next time.
        }
    }

    public static async Task CheckAsync(Window owner)
    {
        var exe = Environment.ProcessPath;
        var assembly = Assembly.GetExecutingAssembly();
        var repo = Updater.BuiltInRepo(assembly);
        if (exe == null || repo == null || !ExplorerIntegration.IsInstalledCopy(exe) || !AppInstaller.IsInstalled(exe, InstallLayout.ForThisUser())) return;

        var updater = new Updater(repo);
        ReleaseInfo? release;
        try
        {
            release = await updater.GetLatestAsync();
        }
        catch (Exception)
        {
            return; // Offline or GitHub hiccup: try again next start.
        }
        var current = Updater.CurrentVersion(assembly);
        if (release == null || release.Version <= current || release.Find(Updater.AppAsset) == null) return;

        var notes = release.Notes.Trim();
        if (notes.Length > 600) notes = notes.Substring(0, 600) + "\u2026";
        var answer = MessageBox.Show(owner,
            $"Jarvis {release.Version.ToString(3)} is available (you have {current.ToString(3)}).\n\n" +
            (notes.Length > 0 ? notes + "\n\n" : "") + "Update now? It takes about a minute and the app restarts.",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        var folder = Path.Combine(Path.GetTempPath(), "Jarvis-update-" + release.Version);
        try
        {
            owner.Cursor = System.Windows.Input.Cursors.Wait;
            var newExe = await updater.DownloadVerifiedAsync(release, Updater.AppAsset, folder);
            await UpdateAddinAsync(owner, updater, release, folder);

            // A running exe can be renamed but not overwritten.
            if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
            File.Move(exe, exe + ".old");
            File.Move(newExe, exe);
        }
        catch (PdmException ex)
        {
            MessageBox.Show(owner, ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(owner, "The update couldn't be installed: " + ex.Message + "\n\nJarvis keeps working on the current version.",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!File.Exists(exe) && File.Exists(exe + ".old")) File.Move(exe + ".old", exe);
            return;
        }
        finally
        {
            owner.Cursor = null;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    /// <summary>Updates the SolidWorks add-in too, if it's installed and older (Windows asks for permission).</summary>
    private static async Task UpdateAddinAsync(Window owner, Updater updater, ReleaseInfo release, string folder)
    {
        var addinDll = SolidWorksAddinSetup.AddinDll;
        if (!File.Exists(addinDll) || release.Find(Updater.AddinAsset) == null) return;
        var installed = Updater.ParseVersion(FileVersionInfo.GetVersionInfo(addinDll).ProductVersion) ?? new Version(0, 0, 0, 0);
        if (installed >= release.Version) return;

        while (SolidWorksAddinSetup.IsSolidWorksRunning)
        {
            var close = MessageBox.Show(owner,
                "The SolidWorks add-in will be updated too. Close SolidWorks (save your work first), then click OK.\n\nCancel skips the add-in for now.",
                "Update the SolidWorks add-in", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (close != MessageBoxResult.OK) return;
        }

        await SolidWorksAddinSetup.InstallAsync(updater, release, folder, showConsole: true);
    }
}
