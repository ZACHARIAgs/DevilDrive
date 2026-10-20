using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Jarvis.Core.Errors;
using Jarvis.Core.Updates;
using Microsoft.Win32;

namespace Jarvis.Client.Services;

/// <summary>
/// The SolidWorks add-in: is SolidWorks on this PC, is the add-in installed, and installing it from a
/// release's Jarvis-SolidWorksAddin.zip (checked against SHA256SUMS) with scripts\Install-SolidWorksAddin.ps1.
/// </summary>
internal static class SolidWorksAddinSetup
{
    public static readonly string AddinDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Jarvis", "SolidWorksAddin", "Jarvis.SwAddin.dll");

    public static bool IsAddinInstalled => File.Exists(AddinDll);

    public static bool IsSolidWorksRunning => Process.GetProcessesByName("SLDWORKS").Length > 0;

    /// <summary>Any SolidWorks version is installed: SLDWORKS.exe in the SOLIDWORKS Corp folders or where the registry says.</summary>
    public static bool IsSolidWorksInstalled()
    {
        try
        {
            return CandidateFolders().Any(folder => File.Exists(Path.Combine(folder, "SLDWORKS.exe")));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> CandidateFolders()
    {
        var corp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SOLIDWORKS Corp");
        if (Directory.Exists(corp))
        {
            foreach (var folder in Directory.EnumerateDirectories(corp, "SOLIDWORKS*")) yield return folder;
        }
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks");
        if (root == null) yield break;
        foreach (var name in root.GetSubKeyNames().Where(n => n.StartsWith("SOLIDWORKS 20", StringComparison.OrdinalIgnoreCase)))
        {
            using var setup = root.OpenSubKey(name + @"\Setup");
            if (setup?.GetValue("SolidWorks Folder") is string folder && folder.Length > 0) yield return folder;
        }
    }

    /// <summary>
    /// Downloads the add-in from <paramref name="release"/> and installs it (one Windows permission
    /// prompt). SolidWorks must be closed. Throws a <see cref="PdmException"/> that says what went wrong.
    /// </summary>
    /// <param name="showConsole">Show the installer's console window (the update flow always did).</param>
    public static async Task InstallAsync(Updater updater, ReleaseInfo release, string folder, bool showConsole)
    {
        if (release.Find(Updater.AddinAsset) == null)
        {
            throw new PdmException($"Release {release.Tag} doesn't include the SolidWorks add-in ({Updater.AddinAsset}). Ask whoever maintains Jarvis.");
        }
        var zip = await updater.DownloadVerifiedAsync(release, Updater.AddinAsset, folder);
        var extracted = Path.Combine(folder, "addin");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        ZipFile.ExtractToDirectory(zip, extracted);

        var script = Path.Combine(extracted, "Install-SolidWorksAddin.ps1");
        if (!File.Exists(script)) throw new PdmException($"{Updater.AddinAsset} is missing its installer.");
        var start = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Source \"{Path.Combine(extracted, "SolidWorksAddin")}\"")
        {
            UseShellExecute = showConsole,
            CreateNoWindow = !showConsole,
            RedirectStandardOutput = !showConsole,
            RedirectStandardError = !showConsole,
        };
        using var install = Process.Start(start) ?? throw new PdmException("The add-in installer didn't start.");
        var output = showConsole ? Task.FromResult("") : install.StandardOutput.ReadToEndAsync();
        var errors = showConsole ? Task.FromResult("") : install.StandardError.ReadToEndAsync();
        await install.WaitForExitAsync();
        var log = ((await output) + (await errors)).Trim();
        if (install.ExitCode != 0 && !showConsole)
        {
            throw new PdmException(log.IndexOf("SolidWorks is running", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Close SolidWorks (save your work first), then try again."
                : "The SolidWorks add-in wasn't installed (Windows' permission prompt was cancelled, or registering it failed). Click the button to try again.", log);
        }
    }
}
