using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Updates;

namespace CapstonePdm.Client.Services;

/// <summary>
/// Checks GitHub Releases at startup. When there's a newer version it asks, downloads the exe (and the
/// SolidWorks add-in if it's installed), checks both against the release's SHA256SUMS, swaps the exe
/// and restarts. Only the installed copy updates itself, not a development build.
/// </summary>
public static class AppUpdater
{
    private static readonly string AddinDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CapstonePDM", "SolidWorksAddin", "CapstonePdm.SwAddin.dll");

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
        if (exe == null || repo == null || !ExplorerIntegration.IsInstalledCopy(exe)) return;

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
        if (notes.Length > 600) notes = notes.Substring(0, 600) + "…";
        var answer = MessageBox.Show(owner,
            $"Capstone PDM {release.Version.ToString(3)} is available (you have {current.ToString(3)}).\n\n" +
            (notes.Length > 0 ? notes + "\n\n" : "") + "Update now? It takes about a minute and the app restarts.",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        var folder = Path.Combine(Path.GetTempPath(), "CapstonePDM-update-" + release.Version);
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
            MessageBox.Show(owner, "The update couldn't be installed: " + ex.Message + "\n\nCapstone PDM keeps working on the current version.",
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
        if (!File.Exists(AddinDll) || release.Find(Updater.AddinAsset) == null) return;
        var installed = Updater.ParseVersion(FileVersionInfo.GetVersionInfo(AddinDll).ProductVersion) ?? new Version(0, 0, 0, 0);
        if (installed >= release.Version) return;

        while (Process.GetProcessesByName("SLDWORKS").Length > 0)
        {
            var close = MessageBox.Show(owner,
                "The SolidWorks add-in will be updated too. Close SolidWorks (save your work first), then click OK.\n\nCancel skips the add-in for now.",
                "Update the SolidWorks add-in", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (close != MessageBoxResult.OK) return;
        }

        var zip = await updater.DownloadVerifiedAsync(release, Updater.AddinAsset, folder);
        var extracted = Path.Combine(folder, "addin");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        ZipFile.ExtractToDirectory(zip, extracted);

        var script = Path.Combine(extracted, "Install-SolidWorksAddin.ps1");
        if (!File.Exists(script)) throw new PdmException($"{Updater.AddinAsset} is missing its installer.");
        var install = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Source \"{Path.Combine(extracted, "SolidWorksAddin")}\"")
        {
            UseShellExecute = true,
        });
        if (install != null) await install.WaitForExitAsync();
    }
}
