using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Jarvis.Core.Errors;
using Jarvis.Core.Git;
using Jarvis.Core.Updates;
using Jarvis.Core.Vault;

namespace Jarvis.Client.Views;

/// <summary>
/// The vault's team: who has access (from GitHub), invitations waiting to be accepted, and, for the
/// repo's admins, adding people by GitHub username and removing them.
/// </summary>
public partial class TeamWindow : Window
{
    private sealed class Row
    {
        public TeamMember Member { get; init; } = null!;
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public Brush DetailBrush { get; init; } = Brushes.Gray;
        public string Initials { get; init; } = "";
        public Brush AvatarBrush { get; init; } = Brushes.Gray;
        public string ActionText { get; init; } = "";
        public Visibility ActionVisibility { get; init; }
    }

    private static readonly Brush Good = Frozen(0x15, 0x80, 0x3D);
    private static readonly Brush Bad = Frozen(0xB9, 0x1C, 0x1C);
    private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
    private static readonly Brush Pending = Frozen(0xB4, 0x53, 0x09);
    private static readonly Brush[] AvatarColors =
    {
        Frozen(0x25, 0x63, 0xEB), Frozen(0x0F, 0x76, 0x6E), Frozen(0x7C, 0x3A, 0xED), Frozen(0xC2, 0x41, 0x0C),
        Frozen(0xBE, 0x18, 0x5D), Frozen(0x4D, 0x7C, 0x0F), Frozen(0x03, 0x69, 0xA1), Frozen(0x92, 0x40, 0x0E),
    };

    private readonly string _vaultName;
    private readonly string? _remoteUrl;
    private readonly string _localPath;
    private readonly IReadOnlyDictionary<string, string>? _displayNames;
    private GitHubApi? _api;
    private GitHubRepo? _repo;
    private string? _me;
    private bool _canManage;
    private bool _busy;

    public TeamWindow(string vaultName, string? remoteUrl, string localPath, IReadOnlyDictionary<string, string>? displayNames = null)
    {
        InitializeComponent();
        _vaultName = vaultName;
        _remoteUrl = remoteUrl;
        _localPath = localPath;
        _displayNames = displayNames;
        HeaderText.Text = $"{vaultName} team";
        SubText.Text = VaultInvites.ParseRepoUrl(remoteUrl) is { } repo ? $"Everyone with access to {repo.Owner}/{repo.Name} on GitHub." : "";
        SetEnabled();
        Loaded += async (_, _) => await LoadAsync();
    }

    private string RepoFullName => _repo?.FullName ?? (VaultInvites.ParseRepoUrl(_remoteUrl) is { } r ? $"{r.Owner}/{r.Name}" : "?");

    private async Task LoadAsync()
    {
        SetBusy(true, "Getting the team from GitHub\u2026", Muted);
        try
        {
            if (_api == null)
            {
                var parsed = VaultInvites.ParseRepoUrl(_remoteUrl)
                             ?? throw new PdmException("This vault isn't on github.com, so Jarvis can't show or change its team. Manage it on the repo's page instead.");
                _api = await GitHubApi.SignedInAsync();
                _me = (await _api.GetUserAsync()).Login;
                _repo = await _api.GetRepoAsync(parsed.Owner, parsed.Name)
                        ?? throw new PdmException($"GitHub didn't find {parsed.Owner}/{parsed.Name} for your account. Make sure you're signed in with an account on this vault's team.");
                _canManage = _repo.CanInvite;
            }
            var parts = _repo!.FullName.Split('/');
            var team = await VaultInvites.LoadTeamAsync(_api, parts[0], parts[1], _displayNames);
            Show(team);
            SetBusy(false, _canManage ? "" : "Only the vault repo's admins can add or remove people. Ask one of them (shown as Admin).", Muted);
        }
        catch (PdmException ex)
        {
            _canManage = false;
            SetBusy(false, ex.Message, Bad);
        }
    }

    private void Show(IReadOnlyList<TeamMember> team)
    {
        MemberList.Items.Clear();
        foreach (var member in team)
        {
            var isMe = string.Equals(member.Login, _me, StringComparison.OrdinalIgnoreCase);
            var name = member.DisplayName ?? member.Login;
            var canAct = _canManage && !member.IsOwner && !isMe;
            MemberList.Items.Add(new Row
            {
                Member = member,
                Title = name + (isMe ? "  (you)" : ""),
                Detail = (member.DisplayName != null ? "@" + member.Login + "  \u2022  " : "") + member.RoleText,
                DetailBrush = member.IsPending ? Pending : Muted,
                Initials = Initials(name),
                AvatarBrush = member.IsPending ? Frozen(0x9C, 0xA3, 0xAF) : AvatarColors[(int)((uint)StableHash(member.Login) % AvatarColors.Length)],
                ActionText = member.IsPending ? "Cancel invite" : "Remove",
                ActionVisibility = canAct ? Visibility.Visible : Visibility.Collapsed,
            });
        }
        var members = team.Count(m => !m.IsPending);
        var pending = team.Count - members;
        CountText.Text = $"{members} member{(members == 1 ? "" : "s")}" + (pending > 0 ? $", {pending} invited" : "");
    }

    private async void OnAdd(object sender, RoutedEventArgs e) => await AddAsync();

    private async void OnUsersKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddAsync();
    }

    private async Task AddAsync()
    {
        if (_busy || !_canManage || _api == null || _repo == null) return;
        var (valid, invalid) = VaultInvites.ParseUsernames(UsersBox.Text);
        if (valid.Count == 0 && invalid.Count == 0)
        {
            SetBusy(false, "Type the GitHub usernames of the people to add.", Bad);
            UsersBox.Focus();
            return;
        }

        SetBusy(true, $"Inviting {string.Join(", ", valid)}\u2026", Muted);
        var parts = _repo.FullName.Split('/');
        var results = await VaultInvites.InviteAsync(_api, parts[0], parts[1], valid);
        var problems = invalid.Select(name => $"\u2022 {name}: not a GitHub username (letters, numbers and single dashes)")
            .Concat(results.Where(r => !r.Succeeded).Select(r => $"\u2022 {r.User}: {r.Message}"))
            .ToList();
        var invited = results.Where(r => r.Outcome == InviteOutcome.Invited).Select(r => r.User).ToList();
        UsersBox.Text = string.Join(", ", invalid.Concat(results.Where(r => !r.Succeeded).Select(r => r.User)));

        await LoadAsync();
        var lines = new List<string>();
        if (invited.Count > 0)
        {
            lines.Add($"Invited {string.Join(", ", invited)}. GitHub emailed them; click \"Copy welcome message\" and send them the steps.");
        }
        if (problems.Count > 0) lines.Add("Couldn't add:\n" + string.Join("\n", problems));
        if (lines.Count == 0) lines.Add("They already have access.");
        SetBusy(false, string.Join("\n", lines), problems.Count > 0 ? Bad : Good);
    }

    private async void OnMemberAction(object sender, RoutedEventArgs e)
    {
        if (_busy || _api == null || _repo == null || !((sender as FrameworkElement)?.Tag is Row { Member: var member })) return;
        var name = member.DisplayName ?? member.Login;
        var question = member.IsPending
            ? $"Cancel {name}'s invitation to {_vaultName}?"
            : $"Remove {name} from {_vaultName}?\n\nThey keep the files already on their PC, but can't Get Latest or check in anymore. " +
              "Anything they have checked out stays checked out until an admin uses Break Lock.";
        if (MessageBox.Show(this, question, "Team", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        var parts = _repo.FullName.Split('/');
        SetBusy(true, member.IsPending ? "Cancelling the invitation\u2026" : $"Removing {name}\u2026", Muted);
        try
        {
            if (member.InvitationId is { } id) await _api.CancelInvitationAsync(parts[0], parts[1], id);
            else await _api.RemoveCollaboratorAsync(parts[0], parts[1], member.Login);
            await LoadAsync();
            SetBusy(false, member.IsPending ? $"Cancelled {name}'s invitation." : $"Removed {name} from the team.", Good);
        }
        catch (PdmException ex)
        {
            SetBusy(false, ex.Message, Bad);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var message = VaultInvites.Message(_vaultName, RepoFullName, _localPath, VaultInvites.InstallLink(Updater.BuiltInRepo(Assembly.GetExecutingAssembly())));
        try
        {
            Clipboard.SetText(message);
            SetBusy(false, "Welcome message copied. Paste it into Discord, email or a text to your new teammates.", Good);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetBusy(false, "Couldn't copy (another program is using the clipboard). Try again.", Bad);
        }
    }

    private void SetBusy(bool busy, string status, Brush color)
    {
        _busy = busy;
        StatusText.Text = status;
        StatusText.Foreground = color;
        Cursor = busy ? Cursors.Wait : null;
        SetEnabled();
    }

    private void SetEnabled()
    {
        AddButton.IsEnabled = UsersBox.IsEnabled = !_busy && _canManage;
        AddPanel.Opacity = _canManage ? 1 : 0.6;
        MemberList.IsEnabled = !_busy;
    }

    private static string Initials(string name)
    {
        var words = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        var letters = words.Length >= 2 ? $"{words[0][0]}{words[1][0]}" : name.Length > 0 ? name.Substring(0, Math.Min(2, name.Length)) : "?";
        return letters.ToUpperInvariant();
    }

    /// <summary>Same color for the same person every time (string.GetHashCode changes per run).</summary>
    private static int StableHash(string text) => text.ToLowerInvariant().Aggregate(17, (hash, c) => unchecked(hash * 31 + c));

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
