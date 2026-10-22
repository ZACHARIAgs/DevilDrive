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
        if (release == null) return;
        if (release.Version <= current || release.Find(Updater.AppAsset) == null)
        {
            await OfferAddinUpdateAsync(owner, updater, release, current);
            return;
        }

        var notes = Updater.WhatsNew(release.Notes);
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

    /// <summary>
    /// The app is up to date but the add-in is older (its update was skipped, e.g. SolidWorks was open):
    /// offer it again, so the add-in always ends up on the same version as the app.
    /// </summary>
    private static async Task OfferAddinUpdateAsync(Window owner, Updater updater, ReleaseInfo release, Version current)
    {
        var installed = SolidWorksAddinSetup.InstalledVersion;
        if (installed == null || installed >= current || installed >= release.Version || release.Find(Updater.AddinAsset) == null) return;
        var answer = MessageBox.Show(owner,
            $"The SolidWorks add-in is version {installed.ToString(3)}, but Jarvis is {current.ToString(3)}. Update the add-in now so they match?\n\n" +
            "Windows asks for permission once. SolidWorks has to be closed.",
            "Update the SolidWorks add-in", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            owner.Cursor = System.Windows.Input.Cursors.Wait;
            await UpdateAddinAsync(owner, updater, release, Path.Combine(Path.GetTempPath(), "Jarvis-addin-" + release.Version));
        }
        catch (Exception ex) when (ex is PdmException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(owner, "The add-in wasn't updated: " + ex.Message + "\n\nJarvis offers it again next time it starts.",
                "Update the SolidWorks add-in", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            owner.Cursor = null;
        }
    }

    /// <summary>Updates the SolidWorks add-in too, if it's installed and older (Windows asks for permission).</summary>
    private static async Task UpdateAddinAsync(Window owner, Updater updater, ReleaseInfo release, string folder)
    {
        var installed = SolidWorksAddinSetup.InstalledVersion;
        if (installed == null || release.Find(Updater.AddinAsset) == null || installed >= release.Version) return;

        while (SolidWorksAddinSetup.IsSolidWorksRunning)
        {
            var close = MessageBox.Show(owner,
                "The SolidWorks add-in will be updated too. Close SolidWorks (save your work first), then click OK.\n\nCancel skips the add-in for now (Jarvis offers it again next time it starts).",
                "Update the SolidWorks add-in", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (close != MessageBoxResult.OK) return;
        }

        await SolidWorksAddinSetup.InstallAsync(updater, release, folder, showConsole: true);
    }
}
