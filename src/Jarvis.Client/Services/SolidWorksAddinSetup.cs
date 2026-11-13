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
///
/// For online release installs, the installer runs as administrator, so nothing it runs or copies is trusted from the Temp folder:
/// the script is the copy built into this exe (its hash is checked again in the elevated process before it runs),
/// and each DLL's hash, taken from the checked zip in memory, is checked again once it's in the install folder.
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

        // 1. Check for folder containing DevilDrive.SwAddin.dll or Jarvis.SwAddin.dll
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
    /// Installs the add-in from local files or from the latest release (welcome screen and Settings).
    /// Throws a <see cref="PdmException"/> that says what to do if it didn't work.
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

        var updater = Updater.ForBuild(Assembly.GetExecutingAssembly())
                      ?? throw new PdmException("This build of DevilDrive doesn't know where to download the add-in from, and no local add-in files were found. Run scripts\\Install-SolidWorksAddin.ps1 instead.");
        var release = await updater.GetLatestAsync()
                      ?? throw new PdmException("Couldn't get the add-in from GitHub. Check your internet connection and try again.");
        try
        {
            await InstallAsync(updater, release, Path.Combine(Path.GetTempPath(), "DevilDrive-addin-" + release.Version));
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
    public static async Task InstallAsync(Updater updater, ReleaseInfo release, string folder)
    {
        if (release.Find(Updater.AddinAsset) == null)
        {
            throw new PdmException($"Release {release.Tag} doesn't include the SolidWorks add-in ({Updater.AddinAsset}). Ask whoever maintains DevilDrive.");
        }
        if (IsSolidWorksRunning) throw new PdmException("Close SolidWorks (save your work first), then try again.");

        // The zip is checked in memory and unpacked from memory, so the hashes below are of the genuine files.
        var zipBytes = await updater.DownloadVerifiedBytesAsync(release, Updater.AddinAsset);
        var extracted = Path.Combine(folder, "addin");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        var source = Path.Combine(extracted, "SolidWorksAddin");
        Directory.CreateDirectory(source);
        var hashes = new List<string>();
        using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                var parts = entry.FullName.Replace('\\', '/').Split('/');
                if (parts.Length != 2 || parts[0] != "SolidWorksAddin" || !parts[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (parts[1].IndexOfAny(new[] { '=', ';', '\'' }) >= 0) throw new PdmException($"{Updater.AddinAsset} has a file with an unexpected name ({parts[1]}).");
                using var memory = new MemoryStream();
                using (var stream = entry.Open()) await stream.CopyToAsync(memory);
                var bytes = memory.ToArray();
                File.WriteAllBytes(Path.Combine(source, parts[1]), bytes);
                hashes.Add(parts[1] + "=" + Updater.Sha256(bytes));
            }
        }
        if (!hashes.Any(h => h.StartsWith("DevilDrive.SwAddin.dll=", StringComparison.OrdinalIgnoreCase) || h.StartsWith("Jarvis.SwAddin.dll=", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PdmException($"{Updater.AddinAsset} is missing the add-in.");
        }

        var scriptBytes = InstallerScript();
        var script = Path.Combine(extracted, "Install-SolidWorksAddin.ps1");
        File.WriteAllBytes(script, scriptBytes);

        // "Load at startup" is per user: set here, so it lands in this account even if the permission prompt is answered with another one.
        using (var startup = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + AddinGuid))
        {
            startup.SetValue("", 1, RegistryValueKind.DWord);
        }

        // The elevated PowerShell reads the script once, checks those bytes, and runs exactly those bytes.
        static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        var command =
            $"$b = [IO.File]::ReadAllBytes({Quote(script)}); " +
            "$h = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-', ''); " +
            $"if ($h -ne {Quote(Updater.Sha256(scriptBytes))}) {{ Write-Host 'The installer was changed on disk, so it was not run.' -ForegroundColor Red; Start-Sleep -Seconds 6; exit 9 }}; " +
            $"& ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($b))) -Elevated -Source {Quote(source)} -Hashes {Quote(string.Join(";", hashes))}";
        var start = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)))
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        Process install;
        try
        {
            install = Process.Start(start) ?? throw new PdmException("The add-in installer didn't start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PdmException("The SolidWorks add-in wasn't installed: Windows' permission prompt was cancelled. Try again and click Yes.", ex.ToString());
        }
        using (install)
        {
            await install.WaitForExitAsync();
            if (install.ExitCode == 9) throw new PdmException("The add-in installer was changed on disk after DevilDrive checked it, so it wasn't run. Try again; if it keeps happening, something on this PC is changing files in your Temp folder.");
            if (install.ExitCode != 0) throw new PdmException("The SolidWorks add-in wasn't installed (registering it failed; the installer's window said why). Close SolidWorks and try again.");
        }
    }

    public const string AddinGuid = "{843995B8-683B-4864-9733-A8E3A87F5ABC}";

    /// <summary>scripts\Install-SolidWorksAddin.ps1 as built into this exe.</summary>
    private static byte[] InstallerScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jarvis.Client.InstallAddin.ps1")
                           ?? throw new PdmException("This build of DevilDrive is missing the add-in installer.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
