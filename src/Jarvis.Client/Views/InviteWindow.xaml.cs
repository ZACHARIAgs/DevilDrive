using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Jarvis.Core.Errors;
using Jarvis.Core.Git;
using Jarvis.Core.Updates;
using Jarvis.Core.Vault;

namespace Jarvis.Client.Views;

/// <summary>Admins add teammates to a vault's GitHub repo and get a message telling them what to do next.</summary>
public partial class InviteWindow : Window
{
    private sealed class Row
    {
        public string Title { get; init; } = "";
        public string Message { get; init; } = "";
        public Brush Color { get; init; } = Brushes.Black;
    }

    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));

    private readonly string? _remoteUrl;
    private GitHubApi? _api;
    private GitHubRepo? _repo;
    private bool _busy;

    public InviteWindow(string vaultName, string? remoteUrl, string localPath)
    {
        InitializeComponent();
        _remoteUrl = remoteUrl;
        var repoName = VaultInvites.ParseRepoUrl(remoteUrl) is { } parsed ? $"{parsed.Owner}/{parsed.Name}" : "?";
        HeaderText.Text = $"Invite teammates to {vaultName}";
        InviteMessageBox.Text = VaultInvites.Message(vaultName, repoName, localPath,
            VaultInvites.InstallLink(Updater.BuiltInRepo(Assembly.GetExecutingAssembly())));
        InviteButton.IsEnabled = false;
        Loaded += async (_, _) => await ConnectAsync();
    }

    private async System.Threading.Tasks.Task ConnectAsync()
    {
        SetBusy(true, "Checking you're an admin of this vault\u2026");
        try
        {
            (_api, _repo) = await VaultInvites.ConnectAsAdminAsync(_remoteUrl);
            SetBusy(false, "");
            UsersBox.Focus();
        }
        catch (PdmException ex)
        {
            SetBusy(false, ex.Message);
            InviteButton.IsEnabled = false;
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        StatusText.Text = status;
        Cursor = busy ? Cursors.Wait : null;
        InviteButton.IsEnabled = !busy && _api != null;
    }

    private async void OnInvite(object sender, RoutedEventArgs e)
    {
        if (_busy || _api == null || _repo == null) return;
        var (valid, invalid) = VaultInvites.ParseUsernames(UsersBox.Text);
        if (valid.Count == 0 && invalid.Count == 0)
        {
            StatusText.Text = "Enter at least one GitHub username.";
            return;
        }

        SetBusy(true, $"Inviting {valid.Count} teammate(s)\u2026");
        ResultList.Items.Clear();
        foreach (var name in invalid)
        {
            ResultList.Items.Add(new Row { Title = "\u2717 " + name, Message = "Not a GitHub username (letters, numbers and single dashes only).", Color = Bad });
        }
        try
        {
            var parts = _repo.FullName.Split('/');
            var results = await VaultInvites.InviteAsync(_api, parts[0], parts[1], valid);
            foreach (var result in results)
            {
                ResultList.Items.Add(new Row
                {
                    Title = (result.Succeeded ? "\u2713 " : "\u2717 ") + result.User,
                    Message = result.Message,
                    Color = result.Succeeded ? Good : Bad,
                });
            }
            var invited = results.Count(r => r.Outcome == InviteOutcome.Invited);
            var failed = results.Count(r => !r.Succeeded) + invalid.Count;
            SetBusy(false, failed == 0
                ? $"Done: {invited} invitation(s) sent. Now send them the message below."
                : $"{failed} couldn't be invited (see below). Check the spelling of their GitHub usernames.");
            // Leave only the ones that failed in the box, to fix and retry.
            UsersBox.Text = string.Join(Environment.NewLine, results.Where(r => !r.Succeeded).Select(r => r.User).Concat(invalid));
        }
        catch (PdmException ex)
        {
            SetBusy(false, ex.Message);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(InviteMessageBox.Text);
            StatusText.Text = "Message copied. Paste it into Discord, email or a text.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "Couldn't copy (another program is using the clipboard). Try again.";
        }
    }
}
