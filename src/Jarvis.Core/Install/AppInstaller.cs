using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Config;

namespace Jarvis.Core.Install;

/// <summary>Where Jarvis installs itself for this Windows account.</summary>
public sealed class InstallLayout
{
    public const string ExeName = "DevilDrive.exe";

    /// <summary>Set to a folder to install there instead (Desktop and Start menu shortcuts go inside it too). For trying the installer.</summary>
    public const string TestRootVariable = "DEVILDRIVE_INSTALL_ROOT";

    public InstallLayout(string installFolder, IReadOnlyList<string> shortcutFolders, bool isTest = false)
    {
        InstallFolder = installFolder;
        ShortcutFolders = shortcutFolders;
        IsTest = isTest;
    }

    /// <summary>%LocalAppData%\Programs\Jarvis (what Setup uses).</summary>
    public string InstallFolder { get; }
    /// <summary>Desktop and Start menu.</summary>
    public IReadOnlyList<string> ShortcutFolders { get; }
    /// <summary>Installing into <see cref="TestRootVariable"/>: leave the rest of Windows alone.</summary>
    public bool IsTest { get; }
    public string ExePath => Path.Combine(InstallFolder, ExeName);

    public static InstallLayout ForThisUser()
    {
        var testRoot = Environment.GetEnvironmentVariable(TestRootVariable);
        if (!string.IsNullOrWhiteSpace(testRoot))
        {
            return new InstallLayout(Path.Combine(testRoot, "Programs", AppFolders.Name),
                new[] { Path.Combine(testRoot, "Desktop"), Path.Combine(testRoot, "Start Menu") }, isTest: true);
        }
        return new InstallLayout(AppFolders.InstallFolder, new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
        });
    }
}

/// <summary>One line of winget's output, made readable.</summary>
public sealed class WingetProgress
{
    public WingetProgress(string text, double? percent)
    {
        Text = text;
        Percent = percent;
    }

    public string Text { get; }
    /// <summary>0-100 while downloading, else null.</summary>
    public double? Percent { get; }
}

/// <summary>
/// What scripts/Setup-Jarvis.ps1 does, for the exe itself: copies Jarvis.exe into the install folder,
/// adds shortcuts and installs Git with winget. The app shows the progress; nothing here has UI.
/// </summary>
public static class AppInstaller
{
    public const string GitPackage = "Git.Git";
    public const string GitLfsPackage = "GitHub.GitLFS";
    public const string GitDownloadPage = "https://git-scm.com/download/win";

    /// <summary>A build from Visual Studio or dotnet (under a bin folder): it never installs or updates itself.</summary>
    public static bool IsDevelopmentBuild(string exePath) =>
        exePath.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The exe runs from the install folder already.</summary>
    public static bool IsInstalled(string exePath, InstallLayout layout) =>
        string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(layout.ExePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>The version of the copy in the install folder, or null if there is none.</summary>
    public static Version? InstalledVersion(InstallLayout layout)
    {
        if (!File.Exists(layout.ExePath)) return null;
        var info = FileVersionInfo.GetVersionInfo(layout.ExePath);
        return Updates.Updater.ParseVersion(info.ProductVersion) ?? new Version(0, 0, 0, 0);
    }

    /// <summary>
    /// Copies the running exe into the install folder. A copy that's running there is renamed to
    /// .old first (it's removed at its next start, like after an update). Clears the "downloaded from
    /// the internet" mark so Windows doesn't warn again for the installed copy.
    /// </summary>
    public static void CopyApp(string sourceExe, InstallLayout layout)
    {
        Directory.CreateDirectory(layout.InstallFolder);
        var target = layout.ExePath;
        if (File.Exists(target))
        {
            try
            {
                File.Delete(target);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Running: a running exe can be renamed but not overwritten.
                if (File.Exists(target + ".old")) File.Delete(target + ".old");
                File.Move(target, target + ".old");
            }
        }
        File.Copy(sourceExe, target);
        RemoveDownloadMark(target);

        var sourceDir = Path.GetDirectoryName(sourceExe);
        if (sourceDir != null)
        {
            var addinZip = Path.Combine(sourceDir, "DevilDrive-SolidWorksAddin.zip");
            if (File.Exists(addinZip))
            {
                try
                {
                    var targetZip = Path.Combine(layout.InstallFolder, "DevilDrive-SolidWorksAddin.zip");
                    File.Copy(addinZip, targetZip, overwrite: true);
                    RemoveDownloadMark(targetZip);
                }
                catch { }
            }
        }
    }

    /// <summary>Deletes the Zone.Identifier stream Windows adds to downloads (what Unblock-File does).</summary>
    public static void RemoveDownloadMark(string path)
    {
        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
        {
            // Not on NTFS, or there's no mark.
        }
    }

    /// <summary>Jarvis.lnk on the Desktop and in the Start menu (replaced if it's there).</summary>
    public static IReadOnlyList<string> CreateShortcuts(InstallLayout layout)
    {
        var created = new List<string>();
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available.");
        var shell = Activator.CreateInstance(shellType)!;
        foreach (var folder in layout.ShortcutFolders)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "DevilDrive.lnk");
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path })!;
            var type = shortcut.GetType();
            void Set(string property, string value) => type.InvokeMember(property, BindingFlags.SetProperty, null, shortcut, new object[] { value });
            Set("TargetPath", layout.ExePath);
            Set("IconLocation", layout.ExePath + ",0");
            Set("WorkingDirectory", layout.InstallFolder);
            Set("Description", "Check SolidWorks files in and out of the Solar Devils vault");
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            created.Add(path);
        }
        return created;
    }

    /// <summary>Reloads PATH from the registry, so Git installed a minute ago is found without restarting.</summary>
    public static void RefreshPath()
    {
        var machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        Environment.SetEnvironmentVariable("Path", machine + ";" + user);
    }

    /// <summary>winget.exe (App Installer), or null on a PC that doesn't have it.</summary>
    public static string? FindWinget()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Append(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));
        foreach (var dir in dirs.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>
    /// Arguments for a silent winget install. <paramref name="userScope"/> installs for this Windows
    /// account only, which needs no administrator permission (Git for Windows supports it).
    /// </summary>
    public static string WingetArguments(string packageId, bool userScope) =>
        $"install --id {packageId} --exact --source winget --silent --disable-interactivity --accept-package-agreements --accept-source-agreements" +
        (userScope ? " --scope user" : "");

    private static readonly Regex DownloadSize = new Regex(@"(?<done>[\d.]+)\s*(?<du>[KMG]B)\s*/\s*(?<total>[\d.]+)\s*(?<tu>[KMG]B)", RegexOptions.Compiled);
    private static readonly Regex Percent = new Regex(@"(?<p>\d{1,3})\s*%", RegexOptions.Compiled);

    /// <summary>
    /// The last readable line of winget output: drops the spinner and progress-bar characters and
    /// works out the download percentage from "12.0 MB / 60.0 MB". Null if there's nothing to show.
    /// </summary>
    public static WingetProgress? ParseWingetOutput(string output)
    {
        var lines = output.Split('\r', '\n')
            .Select(l => new string(l.Where(c => c < '\u2500' || c > '\u259F').ToArray()).Trim()) // box and block characters
            .Where(l => l.Length > 1 || (l.Length == 1 && "-\\|/".IndexOf(l[0]) < 0))
            .ToList();
        if (lines.Count == 0) return null;
        var line = lines[lines.Count - 1];

        var size = DownloadSize.Match(line);
        if (size.Success)
        {
            var done = ToMegabytes(size.Groups["done"].Value, size.Groups["du"].Value);
            var total = ToMegabytes(size.Groups["total"].Value, size.Groups["tu"].Value);
            if (done != null && total is > 0)
            {
                var percent = Math.Min(100, done.Value / total.Value * 100);
                return new WingetProgress($"Downloading ({done:0.#} of {total:0.#} MB)\u2026", percent);
            }
        }
        var p = Percent.Match(line);
        if (p.Success && line.Length < 12 && int.TryParse(p.Groups["p"].Value, out var whole))
        {
            return new WingetProgress($"Working ({whole}%)\u2026", Math.Min(100, whole));
        }
        return new WingetProgress(line, null);
    }

    private static double? ToMegabytes(string number, string unit)
    {
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;
        return unit switch
        {
            "KB" => value / 1024,
            "GB" => value * 1024,
            _ => value,
        };
    }

    /// <summary>
    /// Runs winget with no console window and reports its progress. Returns winget's exit code
    /// (0 = installed). Throws <see cref="Win32Exception"/> if winget doesn't start.
    /// </summary>
    public static async Task<int> RunWingetAsync(string winget, string arguments, IProgress<WingetProgress>? progress, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(winget, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);
        process.Start();

        // winget redraws its progress with \r, so read characters rather than lines.
        async Task Pump(StreamReader reader)
        {
            var buffer = new char[512];
            var recent = new StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                recent.Append(buffer, 0, read);
                if (recent.Length > 4000) recent.Remove(0, recent.Length - 2000);
                var parsed = ParseWingetOutput(recent.ToString());
                if (parsed != null) progress?.Report(parsed);
            }
        }

        var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        using (ct.Register(() => exited.TrySetCanceled()))
        {
            await exited.Task.ConfigureAwait(false);
        }
        await pumps.ConfigureAwait(false);
        return process.ExitCode;
    }
}
