using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Jarvis.Client.Views;
using Jarvis.Core.Errors;
using Jarvis.Core.Install;
using Jarvis.Core.Updates;

namespace Jarvis.Client.Services;

/// <summary>
/// Checks GitHub Releases at startup (and every few hours while open, for the toolbar's Update button). When there's
/// a newer version it asks, downloads the exe (and the SolidWorks add-in if it's installed), checks both against the
/// release's SHA256SUMS, swaps the exe and restarts. Only the copy in the install folder updates itself, not a
/// development build or a download.
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

    /// <summary>The updater for this copy, or null when it doesn't update itself (a development build, or not the installed copy).</summary>
    private static Updater? ForThisCopy()
    {
        var exe = Environment.ProcessPath;
        if (exe == null || AppInstaller.IsDevelopmentBuild(exe) || !AppInstaller.IsInstalled(exe, InstallLayout.ForThisUser())) return null;
        return Updater.ForBuild(Assembly.GetExecutingAssembly());
    }

    private static Version Current => Updater.CurrentVersion(Assembly.GetExecutingAssembly());

    /// <summary>A newer Jarvis this copy can update to, or null (up to date, offline, or a copy that doesn't update itself). Never asks anything.</summary>
    public static async Task<ReleaseInfo?> FindNewerAsync()
    {
        if (ForThisCopy() is not { } updater) return null;
        try
        {
            var release = await updater.GetLatestAsync();
            return release != null && release.Version > Current && release.Find(Updater.AppAsset) != null ? release : null;
        }
        catch (Exception)
        {
            return null; // Offline or a GitHub hiccup: the next check tries again.
        }
    }

    /// <summary>
    /// At startup: offers a newer version (or a matching add-in). Returns the newer release when the user said
    /// Not now, so the toolbar can keep offering it; null otherwise.
    /// </summary>
    public static async Task<ReleaseInfo?> CheckAsync(Window owner)
    {
        if (ForThisCopy() is not { } updater) return null;
        ReleaseInfo? release;
        try
        {
            release = await updater.GetLatestAsync();
        }
        catch (Exception)
        {
            return null; // Offline or GitHub hiccup: the next check tries again.
        }
        var current = Current;
        if (release == null) return null;
        if (release.Version <= current || release.Find(Updater.AppAsset) == null)
        {
            await OfferAddinUpdateAsync(owner, updater, release, current);
            return null;
        }
        if (!AskToUpdate(owner, release)) return release;
        await InstallAsync(owner, release);
        return release; // only reached if the update failed: keep offering it
    }

    /// <summary>"DevilDrive 0.9.0.1 is available... Update now?" with what's new. True for yes.</summary>
    public static bool AskToUpdate(Window owner, ReleaseInfo release)
    {
        var notes = Updater.WhatsNew(release.Notes);
        return ModernMessageBox.Show(owner,
            $"DevilDrive {Updater.Format(release.Version)} is available (you have {Updater.Format(Current)}).\n\n" +
            (notes.Length > 0 ? notes + "\n\n" : "") + "Update now? It takes about a minute and the app restarts.",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
    }

    /// <summary>Downloads and checks the new version (and the add-in), swaps the exe and restarts. Shows what went wrong if it can't.</summary>
    public static async Task InstallAsync(Window owner, ReleaseInfo release)
    {
        var exe = Environment.ProcessPath;
        if (exe == null || ForThisCopy() is not { } updater) return;
        var folder = Path.Combine(Path.GetTempPath(), "DevilDrive-update-" + release.Version);
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
            ModernMessageBox.Show(owner, ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ModernMessageBox.Show(owner, "The update couldn't be installed: " + ex.Message + "\n\nDevilDrive keeps working on the current version.",
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
        var answer = ModernMessageBox.Show(owner,
            $"The SolidWorks add-in is version {Updater.Format(installed)}, but DevilDrive is {Updater.Format(current)}. Update the add-in now so they match?\n\n" +
            "Windows asks for permission once. SolidWorks has to be closed.",
            "Update the SolidWorks add-in", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            owner.Cursor = System.Windows.Input.Cursors.Wait;
            await UpdateAddinAsync(owner, updater, release, Path.Combine(Path.GetTempPath(), "DevilDrive-addin-" + release.Version));
        }
        catch (Exception ex) when (ex is PdmException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ModernMessageBox.Show(owner, "The add-in wasn't updated: " + ex.Message + "\n\nDevilDrive offers it again next time it starts.",
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
            if (SolidWorksAddinSetup.OnlyHiddenSolidWorksRunning)
            {
                // A SolidWorks with no window (left by a crash or another program): the user can't close it themselves.
                var end = ModernMessageBox.Show(owner,
                    "SolidWorks is still running in the background, without a window (left over from a crash or started by another program), " +
                    "so the add-in can't be updated.\n\nEnd it now? Nothing you can see is open in it.",
                    "Update the SolidWorks add-in", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (end != MessageBoxResult.Yes) return;
                SolidWorksAddinSetup.EndHiddenSolidWorks();
                await Task.Delay(2000);
                continue;
            }
            var close = ModernMessageBox.Show(owner,
                "The SolidWorks add-in will be updated too. Close SolidWorks (save your work first), then click OK.\n\nCancel skips the add-in for now (DevilDrive offers it again next time it starts).",
                "Update the SolidWorks add-in", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (close != MessageBoxResult.OK) return;
        }

        await SolidWorksAddinSetup.InstallAsync(updater, release, folder);
    }
}
