using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Jarvis.Core.Git;
using Jarvis.Core.Install;

namespace Jarvis.Client.Services;

/// <summary>Installs Git for Windows (or just Git LFS) with winget, without a console window.</summary>
internal static class GitSetup
{
    /// <summary>
    /// Installs what <paramref name="tools"/> is missing. Returns null when winget ran (check the tools
    /// again to see if it worked), or a message saying what the user has to do instead.
    /// </summary>
    public static async Task<string?> InstallAsync(ToolCheck tools, IProgress<WingetProgress> progress)
    {
        var winget = AppInstaller.FindWinget();
        if (winget == null)
        {
            OpenLink(AppInstaller.GitDownloadPage);
            return "Install Git from the page that just opened (keep the default options; Git LFS is included), then come back here.";
        }

        try
        {
            if (tools.GitPath == null)
            {
                // For this Windows account first (no permission prompt); if that isn't offered, for the whole PC.
                var code = await AppInstaller.RunWingetAsync(winget, AppInstaller.WingetArguments(AppInstaller.GitPackage, userScope: true), progress);
                AppInstaller.RefreshPath();
                if (code != 0 && GitRunner.FindGit() == null)
                {
                    progress.Report(new WingetProgress("Installing Git for Windows for everyone on this PC. Windows will ask for permission\u2026", null));
                    await AppInstaller.RunWingetAsync(winget, AppInstaller.WingetArguments(AppInstaller.GitPackage, userScope: false), progress);
                }
            }
            else
            {
                progress.Report(new WingetProgress("Installing Git LFS. Windows may ask for permission\u2026", null));
                await AppInstaller.RunWingetAsync(winget, AppInstaller.WingetArguments(AppInstaller.GitLfsPackage, userScope: false), progress);
            }
        }
        catch (Win32Exception)
        {
            OpenLink(AppInstaller.GitDownloadPage);
            return "The Git installer didn't start. Install Git from the page that just opened (keep the default options), then come back here.";
        }
        AppInstaller.RefreshPath();
        return null;
    }

    public static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No browser; the message says where to go.
        }
    }
}
