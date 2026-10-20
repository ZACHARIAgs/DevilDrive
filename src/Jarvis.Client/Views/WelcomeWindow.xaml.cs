using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Jarvis.Client.Services;
using Jarvis.Core.Errors;
using Jarvis.Core.Git;
using Jarvis.Core.Install;
using Jarvis.Core.Updates;
using Jarvis.Core.Vault;

namespace Jarvis.Client.Views;

/// <summary>
/// First run, when no vault is on this PC yet: makes sure Git, Git LFS and a GitHub sign-in are ready,
/// fills in the name and email check-ins are signed with, offers the GitHub invitations waiting for the
/// user, then finds the user's vaults (or creates their team's). Offers the SolidWorks add-in too.
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
        Closing += (_, e) => e.Cancel = _busy && DialogResult == null;
    }

    /// <summary>The vault to open once this window closes, if the user picked or created one.</summary>
    public string? OpenFolder { get; private set; }

    /// <summary>A GitHub invitation as the list shows it.</summary>
    private sealed class InvitationItem
    {
        public InvitationItem(GitHubRepoInvitation invitation)
        {
            Invitation = invitation;
            const string prefix = "Jarvis vault: ";
            var description = invitation.Description ?? "";
            Title = description.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && description.Length > prefix.Length
                ? description.Substring(prefix.Length)
                : invitation.RepoFullName;
            var from = invitation.Inviter.Length > 0 ? $"{invitation.RepoFullName}, invited by {invitation.Inviter}" : invitation.RepoFullName;
            Detail = invitation.LooksLikeVault ? from : from + " (may not be a Jarvis vault)";
        }

        public GitHubRepoInvitation Invitation { get; }
        public string Title { get; }
        public string Detail { get; }
    }

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
        await ShowSignedInAsync();
        ShowAddin();
        SetBusy(false);
    }

    /// <summary>After sign-in: the name and email for check-ins, and invitations waiting to be accepted.</summary>
    private async Task ShowSignedInAsync()
    {
        IdentityPanel.Visibility = IdentityText.Visibility = Visibility.Collapsed;
        InvitationList.Visibility = Visibility.Collapsed;
        if (_user == null) return;

        try
        {
            var current = await GitIdentity.GetGlobalAsync();
            if (current.IsComplete)
            {
                ShowIdentity(current);
            }
            else
            {
                var suggested = GitIdentity.Suggest(_user);
                NameBox.Text = current.Name ?? suggested.Name;
                EmailBox.Text = current.Email ?? suggested.Email;
                NameBox.IsReadOnly = current.Name != null;
                EmailBox.IsReadOnly = current.Email != null;
                EmailHint.Visibility = current.Email == null ? Visibility.Visible : Visibility.Collapsed;
                IdentityPanel.Visibility = Visibility.Visible;
            }
        }
        catch (PdmException)
        {
            // Git is missing after all; step 1 says so.
        }

        try
        {
            var api = await GitHubApi.SignedInAsync();
            var items = (await api.GetMyInvitationsAsync())
                .OrderByDescending(i => i.LooksLikeVault)
                .ThenBy(i => i.RepoFullName, StringComparer.OrdinalIgnoreCase)
                .Select(i => new InvitationItem(i))
                .ToList();
            InvitationList.ItemsSource = items;
            InvitationList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            VaultText.Text = items.Count > 0
                ? (items.Count == 1 ? "You've been invited to a vault. Accept the invitation and Jarvis downloads it:" : "You've been invited to these repos. Accept your team's vault and Jarvis downloads it:")
                : "Joining a team? Ask your team lead to invite your GitHub account (" + _user.Login + "), then click Find my vaults. Starting a team? Create its vault and invite your teammates.";
        }
        catch (PdmException)
        {
            // Offline or no permission to list invitations: Find my vaults still works after accepting on github.com.
        }
    }

    private void ShowIdentity(CommitIdentity identity)
    {
        IdentityPanel.Visibility = Visibility.Collapsed;
        IdentityText.Text = $"\u2713 Check-ins are signed as {identity.Name} <{identity.Email}>.";
        IdentityText.Visibility = Visibility.Visible;
    }

    /// <summary>Saves the name and email shown (only the ones Git doesn't have yet). False if they need fixing first.</summary>
    private async Task<bool> SaveIdentityAsync()
    {
        if (IdentityPanel.Visibility != Visibility.Visible) return true;
        var problem = GitIdentity.Validate(NameBox.Text, EmailBox.Text);
        if (problem != null)
        {
            StatusText.Text = problem;
            return false;
        }
        try
        {
            ShowIdentity(await GitIdentity.SetGlobalIfMissingAsync(NameBox.Text, EmailBox.Text));
            return true;
        }
        catch (PdmException ex)
        {
            StatusText.Text = ex.Message;
            return false;
        }
    }

    private void ShowAddin()
    {
        AddinPanel.Visibility = !SolidWorksAddinSetup.IsAddinInstalled && SolidWorksAddinSetup.IsSolidWorksInstalled()
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.Wait : null;
        GitButton.IsEnabled = AddinButton.IsEnabled = !busy;
        SignInButton.IsEnabled = !busy && _tools.Ready;
        FindButton.IsEnabled = NewButton.IsEnabled = InvitationList.IsEnabled = !busy && _tools.Ready && _user != null;
    }

    private async void OnInstallGit(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        StatusText.Text = "";
        SetBusy(true);
        GitText.Text = _tools.GitPath == null ? "Installing Git for Windows (includes Git LFS)\u2026" : "Installing Git LFS\u2026";
        GitProgress.Visibility = Visibility.Visible;
        GitProgress.IsIndeterminate = true;
        var progress = new Progress<WingetProgress>(p =>
        {
            GitText.Text = p.Text;
            GitProgress.IsIndeterminate = p.Percent == null;
            if (p.Percent is { } percent) GitProgress.Value = percent;
        });
        var instead = await GitSetup.InstallAsync(_tools, progress);
        GitProgress.Visibility = Visibility.Collapsed;
        if (instead != null)
        {
            StatusText.Text = instead;
            await CheckAsync();
            await WaitForUserAsync();
            return;
        }
        await CheckAsync();
        if (!_tools.Ready && StatusText.Text.Length == 0)
        {
            StatusText.Text = "Git still isn't ready. If it just finished installing, restart your PC and open Jarvis again.";
        }
    }

    private async Task WaitForUserAsync()
    {
        // Re-check every few seconds while the user installs Git themselves.
        for (var i = 0; i < 120 && IsVisible && !_tools.Ready; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            AppInstaller.RefreshPath();
            if (!_busy) await CheckAsync();
        }
        if (_tools.Ready) StatusText.Text = "";
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
            await ShowSignedInAsync();
        }
        catch (PdmException ex)
        {
            SignInText.Text = "Not signed in yet.";
            StatusText.Text = ex.Message;
        }
        SetBusy(false);
    }

    private async void OnAccept(object sender, RoutedEventArgs e)
    {
        if (_busy || !(sender is FrameworkElement { Tag: InvitationItem item })) return;
        StatusText.Text = "";
        if (!await SaveIdentityAsync()) return;
        SetBusy(true);
        var progress = new Progress<string>(text => VaultText.Text = text);
        try
        {
            var api = await GitHubApi.SignedInAsync();
            var vault = await VaultCatalog.AcceptInvitationAsync(api, item.Invitation, progress);
            if (vault == null)
            {
                SetBusy(false);
                await ShowSignedInAsync();
                StatusText.Text = $"You've joined {item.Invitation.RepoFullName}, but it isn't a Jarvis vault (it has no pdm.json), so nothing was downloaded.";
                return;
            }
            OpenFolder = await VaultCatalog.DownloadAsync(vault, progress);
            SetBusy(false);
            DialogResult = true;
        }
        catch (PdmException ex)
        {
            SetBusy(false);
            await ShowSignedInAsync();
            StatusText.Text = ex.Message + (ex.Message.Contains("isn't empty") ? " Then use Find my vaults." : "");
        }
    }

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "";
        if (await SaveIdentityAsync()) ShowVaults(findNow: true, createNew: false);
    }

    private async void OnNew(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "";
        if (await SaveIdentityAsync()) ShowVaults(findNow: false, createNew: true);
    }

    private void ShowVaults(bool findNow, bool createNew)
    {
        var vaults = new VaultsWindow(findNow, createNew) { Owner = this };
        vaults.ShowDialog();
        if (vaults.OpenFolder == null) return;
        OpenFolder = vaults.OpenFolder;
        DialogResult = true;
    }

    private async void OnInstallAddin(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        StatusText.Text = "";
        if (SolidWorksAddinSetup.IsSolidWorksRunning)
        {
            StatusText.Text = "Close SolidWorks first (save your work), then click Install the SolidWorks add-in again.";
            return;
        }
        var repo = Updater.BuiltInRepo(Assembly.GetExecutingAssembly());
        if (repo == null)
        {
            StatusText.Text = "This build of Jarvis doesn't know where to download the add-in from. Run scripts\\Install-SolidWorksAddin.ps1 instead.";
            return;
        }

        SetBusy(true);
        AddinText.Text = "Downloading the add-in\u2026";
        try
        {
            var updater = new Updater(repo);
            var release = await updater.GetLatestAsync()
                          ?? throw new PdmException("Couldn't get the add-in from GitHub. Check your internet connection and try again.");
            AddinText.Text = "Installing. Click Yes when Windows asks for permission\u2026";
            await SolidWorksAddinSetup.InstallAsync(updater, release, Path.Combine(Path.GetTempPath(), "Jarvis-addin-" + release.Version), showConsole: false);
            if (SolidWorksAddinSetup.IsAddinInstalled)
            {
                AddinText.Text = "\u2713 Installed. Start SolidWorks and look for Jarvis in the Task Pane on the right.";
                AddinButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                AddinText.Text = "The add-in wasn't installed. Click the button to try again.";
            }
        }
        catch (Exception ex) when (ex is PdmException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AddinText.Text = "Check files out and in from SolidWorks' task pane. Close SolidWorks first; Windows asks for permission once.";
            StatusText.Text = ex.Message;
        }
        SetBusy(false);
    }
}
