using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jarvis.Core.Errors;
using Jarvis.Core.Updates;
using Microsoft.Win32;

namespace Jarvis.Client.Services;

/// <summary>
/// The SolidWorks add-in: is SolidWorks on this PC, is the add-in installed, and installing it from a
/// local folder/zip or a release's DevilDrive-SolidWorksAddin.zip (checked against SHA256SUMS) with scripts\Install-SolidWorksAddin.ps1.
/// </summary>
internal static class SolidWorksAddinSetup
{
    public static readonly string AddinDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DevilDrive", "SolidWorksAddin", "DevilDrive.SwAddin.dll");

    public static bool IsAddinInstalled => File.Exists(AddinDll) || File.Exists(LegacyAddinDll);

    private static string LegacyAddinDll => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DevilDrive", "SolidWorksAddin", "Jarvis.SwAddin.dll");

    /// <summary>Version of the installed add-in, or null if it isn't installed.</summary>
    public static Version? InstalledVersion =>
        IsAddinInstalled ? Updater.ParseVersion(FileVersionInfo.GetVersionInfo(File.Exists(AddinDll) ? AddinDll : LegacyAddinDll).ProductVersion) ?? new Version(0, 0, 0, 0) : null;

    public static bool IsSolidWorksRunning => Process.GetProcessesByName("SLDWORKS").Length > 0;

    /// <summary>
    /// SolidWorks is running, but only hidden (no window): left behind by a crash or started by another program.
    /// The user can't close it themselves, so they're offered <see cref="EndHiddenSolidWorks"/>.
    /// </summary>
    public static bool OnlyHiddenSolidWorksRunning
    {
        get
        {
            var running = Process.GetProcessesByName("SLDWORKS");
            try
            {
                return running.Length > 0 && running.All(p => p.MainWindowHandle == IntPtr.Zero);
            }
            finally
            {
                foreach (var p in running) p.Dispose();
            }
        }
    }

    /// <summary>Ends SolidWorks processes that have no window (nothing the user can see is open in them).</summary>
    public static void EndHiddenSolidWorks()
    {
        foreach (var process in Process.GetProcessesByName("SLDWORKS"))
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero) process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone, or not ours to end.
                }
            }
        }
    }

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
    /// Installs the add-in using a local PowerShell installer script and add-in files folder.
    /// </summary>
    private static async Task InstallFromLocalFilesAsync(string scriptPath, string sourceFolder, bool showConsole)
    {
        var start = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Source \"{sourceFolder}\"")
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

    /// <summary>
    /// Searches local folders and archives for the SolidWorks add-in files before attempting an online download.
    /// </summary>
    public static async Task<bool> TryInstallFromLocalAsync(string? hintPath = null, bool showConsole = false)
    {
        var searchDirs = new List<string>();
        if (!string.IsNullOrEmpty(hintPath))
        {
            if (File.Exists(hintPath))
            {
                var dir = Path.GetDirectoryName(hintPath);
                if (dir != null) searchDirs.Add(dir);
            }
            else if (Directory.Exists(hintPath))
            {
                searchDirs.Add(hintPath);
            }
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (dir != null) searchDirs.Add(dir);
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir)) searchDirs.Add(baseDir);

        var installFolder = Jarvis.Core.Install.InstallLayout.ForThisUser().InstallFolder;
        if (!string.IsNullOrEmpty(installFolder)) searchDirs.Add(installFolder);

        // Also check parent directories for dev/staging environments
        foreach (var dir in searchDirs.ToArray())
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent != null)
            {
                searchDirs.Add(parent);
                searchDirs.Add(Path.Combine(parent, "SolidWorksAddin"));
                searchDirs.Add(Path.Combine(parent, "publish-addin"));
                searchDirs.Add(Path.Combine(parent, "DevilDrive-Setup", "SolidWorksAddin"));
            }
        }

        // 1. Check for folder containing Jarvis.SwAddin.dll
        foreach (var dir in searchDirs.Distinct())
        {
            if (!Directory.Exists(dir)) continue;

            string? addinDir = null;
            if (File.Exists(Path.Combine(dir, "DevilDrive.SwAddin.dll")) || File.Exists(Path.Combine(dir, "Jarvis.SwAddin.dll")))
            {
                addinDir = dir;
            }
            else if (Directory.Exists(Path.Combine(dir, "SolidWorksAddin")) &&
                     (File.Exists(Path.Combine(dir, "SolidWorksAddin", "DevilDrive.SwAddin.dll")) ||
                      File.Exists(Path.Combine(dir, "SolidWorksAddin", "Jarvis.SwAddin.dll"))))
            {
                addinDir = Path.Combine(dir, "SolidWorksAddin");
            }
            else if (Directory.Exists(Path.Combine(dir, "publish-addin")) &&
                     (File.Exists(Path.Combine(dir, "publish-addin", "DevilDrive.SwAddin.dll")) ||
                      File.Exists(Path.Combine(dir, "publish-addin", "Jarvis.SwAddin.dll"))))
            {
                addinDir = Path.Combine(dir, "publish-addin");
            }

            if (addinDir != null)
            {
                var scriptCandidates = new[]
                {
                    Path.Combine(addinDir, "Install-SolidWorksAddin.ps1"),
                    Path.Combine(addinDir, "..", "Install-SolidWorksAddin.ps1"),
                    Path.Combine(addinDir, "..", "scripts", "Install-SolidWorksAddin.ps1"),
                    Path.Combine(addinDir, "..", "..", "scripts", "Install-SolidWorksAddin.ps1"),
                };
                var script = scriptCandidates.FirstOrDefault(File.Exists);
                if (script != null)
                {
                    await InstallFromLocalFilesAsync(Path.GetFullPath(script), Path.GetFullPath(addinDir), showConsole);
                    return true;
                }
            }

            // 2. Check for add-in zip archive
            var zipCandidates = new[]
            {
                Path.Combine(dir, "DevilDrive-SolidWorksAddin.zip"),
                Path.Combine(dir, "Jarvis-SolidWorksAddin.zip"),
            };
            var zip = zipCandidates.FirstOrDefault(File.Exists);
            if (zip == null)
            {
                try
                {
                    zip = Directory.EnumerateFiles(dir, "*SolidWorksAddin*.zip").FirstOrDefault();
                }
                catch { }
            }

            if (zip != null)
            {
                var tempExtract = Path.Combine(Path.GetTempPath(), "DevilDrive-addin-local-" + Guid.NewGuid().ToString("N"));
                try
                {
                    if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true);
                    ZipFile.ExtractToDirectory(zip, tempExtract);

                    var script = Path.Combine(tempExtract, "Install-SolidWorksAddin.ps1");
                    var source = Path.Combine(tempExtract, "SolidWorksAddin");
                    if (File.Exists(script) && Directory.Exists(source))
                    {
                        await InstallFromLocalFilesAsync(script, source, showConsole);
                        return true;
                    }
                }
                finally
                {
                    try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Installs the add-in from local files or from the latest GitHub release (welcome screen and Settings).
    /// </summary>
    public static async Task InstallLatestAsync(string? hintPath = null)
    {
        if (OnlyHiddenSolidWorksRunning)
        {
            throw new PdmException("SolidWorks is still running in the background, without a window (left over from a crash or started by another program). " +
                                   "Open Task Manager (Ctrl+Shift+Esc), right-click SOLIDWORKS, choose End task, then try again.");
        }
        if (IsSolidWorksRunning) throw new PdmException("Close SolidWorks first (save your work), then try again.");

        // Try local install first
        if (await TryInstallFromLocalAsync(hintPath, showConsole: false))
        {
            if (!IsAddinInstalled) throw new PdmException("The SolidWorks add-in wasn't installed. Try again, and click Yes when Windows asks for permission.");
            return;
        }

        // Try GitHub release
        var repo = Updater.BuiltInRepo(Assembly.GetExecutingAssembly());
        if (repo == null)
        {
            throw new PdmException("The SolidWorks add-in files were not found locally. To install, run scripts\\Install-SolidWorksAddin.ps1 from PowerShell.");
        }
        var updater = new Updater(repo);
        ReleaseInfo? release = null;
        try
        {
            release = await updater.GetLatestAsync();
        }
        catch (Exception ex)
        {
            throw new PdmException($"Could not check for add-in releases on GitHub ({repo}): {ex.Message}. You can install the add-in locally by running scripts\\Install-SolidWorksAddin.ps1.");
        }
        if (release == null)
        {
            throw new PdmException($"No releases found on GitHub for {repo} yet. To install the add-in locally, run scripts\\Install-SolidWorksAddin.ps1 from PowerShell.");
        }
        try
        {
            await InstallAsync(updater, release, Path.Combine(Path.GetTempPath(), "DevilDrive-addin-" + release.Version), showConsole: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new PdmException("The add-in couldn't be unpacked: " + ex.Message, ex.ToString());
        }
        if (!IsAddinInstalled) throw new PdmException("The SolidWorks add-in wasn't installed. Try again, and click Yes when Windows asks for permission.");
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
            throw new PdmException($"Release {release.Tag} doesn't include the SolidWorks add-in ({Updater.AddinAsset}). Ask whoever maintains DevilDrive.");
        }
        var zip = await updater.DownloadVerifiedAsync(release, Updater.AddinAsset, folder);
        var extracted = Path.Combine(folder, "addin");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        ZipFile.ExtractToDirectory(zip, extracted);

        var script = Path.Combine(extracted, "Install-SolidWorksAddin.ps1");
        if (!File.Exists(script)) throw new PdmException($"{Updater.AddinAsset} is missing its installer.");
        await InstallFromLocalFilesAsync(script, Path.Combine(extracted, "SolidWorksAddin"), showConsole);
    }
}
