using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Git;

namespace CapstonePdm.Client.Views;

/// <summary>
/// First run, when no vault is on this PC yet: makes sure Git, Git LFS and a GitHub sign-in are ready,
/// then finds the user's vaults (or creates their team's). The same setup works for every team.
/// </summary>
public partial class WelcomeWindow : Window
{
    private ToolCheck _tools = new ToolCheck();
    private GitHubUser? _user;
    private bool _busy;

    public WelcomeWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await CheckAsync();
    }

    /// <summary>The vault to open once this window closes, if the user picked or created one.</summary>
    public string? OpenFolder { get; private set; }

    private async Task CheckAsync()
    {
        SetBusy(true);
        _tools = await GitHubSignIn.CheckToolsAsync();
        if (_tools.GitPath == null) GitText.Text = "Git isn't installed yet. Click Install Git (Windows may ask for permission).";
        else if (_tools.LfsVersion == null) GitText.Text = $"{_tools.GitVersion} is installed, but Git LFS isn't. Click Install Git LFS.";
        else GitText.Text = "\u2713 Ready: " + _tools.GitVersion;
        GitButton.Content = _tools.GitPath == null ? "Install Git" : "Install Git LFS";
        GitButton.Visibility = _tools.Ready ? Visibility.Collapsed : Visibility.Visible;

        _user = null;
        if (!_tools.Ready)
        {
            SignInText.Text = "Install Git first.";
        }
        else
        {
            SignInText.Text = "Checking\u2026";
            try
            {
                _user = await GitHubSignIn.GetSignedInUserAsync();
                SignInText.Text = _user != null
                    ? $"\u2713 Signed in as {_user.Login}."
                    : "Not signed in yet. Click Sign in to GitHub (no account? make a free one at github.com).";
            }
            catch (PdmException ex)
            {
                SignInText.Text = ex.Message;
            }
        }
        SignInButton.Visibility = _user == null ? Visibility.Visible : Visibility.Collapsed;
        SetBusy(false);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.Wait : null;
        GitButton.IsEnabled = !busy;
        SignInButton.IsEnabled = !busy && _tools.Ready;
        FindButton.IsEnabled = NewButton.IsEnabled = !busy && _tools.Ready && _user != null;
    }

    private async void OnInstallGit(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        StatusText.Text = "";
        var winget = FindOnPath("winget.exe");
        if (winget == null)
        {
            OpenLink("https://git-scm.com/download/win");
            StatusText.Text = "Install Git from the page that just opened (keep the default options; Git LFS is included), then come back here.";
            await WaitForUserAsync();
            return;
        }

        SetBusy(true);
        var id = _tools.GitPath == null ? "Git.Git" : "GitHub.GitLFS";
        GitText.Text = $"Installing {(id == "Git.Git" ? "Git for Windows" : "Git LFS")}. A command window and a Windows permission prompt may appear\u2026";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(winget,
                $"install --id {id} --exact --source winget --accept-package-agreements --accept-source-agreements") { UseShellExecute = true });
            if (process != null) await process.WaitForExitAsync();
        }
        catch (Win32Exception)
        {
            StatusText.Text = "The installer didn't start. Install Git from https://git-scm.com/download/win, then click Install Git again to re-check.";
        }
        await CheckAsync();
        if (!_tools.Ready && StatusText.Text.Length == 0)
        {
            StatusText.Text = "Git still isn't ready. If it just finished installing, restart your PC and open Capstone PDM again.";
        }
    }

    private async Task WaitForUserAsync()
    {
        // Re-check every few seconds while the user installs Git themselves.
        for (var i = 0; i < 120 && IsVisible && !_tools.Ready; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (!_busy) await CheckAsync();
        }
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = "";
        SignInText.Text = "Sign in to GitHub in the window (or browser tab) that opens\u2026";
        try
        {
            _user = await GitHubSignIn.SignInAsync();
            SignInText.Text = $"\u2713 Signed in as {_user.Login}.";
            SignInButton.Visibility = Visibility.Collapsed;
        }
        catch (PdmException ex)
        {
            SignInText.Text = "Not signed in yet.";
            StatusText.Text = ex.Message;
        }
        SetBusy(false);
    }

    private void OnFind(object sender, RoutedEventArgs e) => ShowVaults(findNow: true, createNew: false);

    private void OnNew(object sender, RoutedEventArgs e) => ShowVaults(findNow: false, createNew: true);

    private void ShowVaults(bool findNow, bool createNew)
    {
        var vaults = new VaultsWindow(findNow, createNew) { Owner = this };
        vaults.ShowDialog();
        if (vaults.OpenFolder == null) return;
        OpenFolder = vaults.OpenFolder;
        DialogResult = true;
    }

    private static string? FindOnPath(string exe)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Append(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));
        foreach (var dir in dirs.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    private static void OpenLink(string url)
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
