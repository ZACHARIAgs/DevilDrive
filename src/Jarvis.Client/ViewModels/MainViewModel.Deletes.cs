using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;

namespace Jarvis.Client.ViewModels;

/// <summary>
/// Deleting files from the vault: anyone asks (with a reason), admins see the requests and delete the
/// files for everyone, or reject the request.
/// </summary>
public sealed partial class MainViewModel
{
    public ICommand RequestDeleteCommand { get; private set; } = null!;
    public ICommand WithdrawDeleteRequestCommand { get; private set; } = null!;
    public ICommand DeleteFromVaultCommand { get; private set; } = null!;
    public ICommand RejectDeleteRequestCommand { get; private set; } = null!;

    private void InitializeDeleteCommands()
    {
        RequestDeleteCommand = new RelayCommand(async () => await RequestDeleteAsync(), () => CanEdit && RequestDeleteTargets().Count > 0);
        WithdrawDeleteRequestCommand = new RelayCommand(async () => await WithdrawDeleteRequestAsync(), () => CanEdit && WithdrawDeleteTargets().Count > 0);
        DeleteFromVaultCommand = new RelayCommand(async () => await DeleteFromVaultAsync(), () => CanEdit && IsAdmin && DeleteFromVaultTargets().Count > 0);
        RejectDeleteRequestCommand = new RelayCommand(async () => await RejectDeleteRequestAsync(), () => CanEdit && IsAdmin && _selected.Any(r => r.File.DeleteRequest != null));
    }

    /// <summary>Files in the vault that nobody has asked to delete yet.</summary>
    private List<FileRowViewModel> RequestDeleteTargets() =>
        _selected.Where(r => r.File.State != FileState.NotInVault && r.File.DeleteRequest == null && !VersionCopies.IsVersionCopy(r.Path)).ToList();

    /// <summary>Your own requests (an admin can take back anyone's).</summary>
    private List<FileRowViewModel> WithdrawDeleteTargets() =>
        _selected.Where(r => r.File.DeleteRequest is { } request &&
                             (IsAdmin || string.Equals(request.RequestedBy, _vault?.GitHubLogin, StringComparison.OrdinalIgnoreCase))).ToList();

    private List<FileRowViewModel> DeleteFromVaultTargets() =>
        _selected.Where(r => r.File.State is not (FileState.NotInVault or FileState.CheckedOutByOther or FileState.Frozen or FileState.InReview) &&
                             !VersionCopies.IsVersionCopy(r.Path)).ToList();

    /// <summary>
    /// Only files nothing else uses can be deleted (the vault checks this too). Tells the user right away which
    /// selected files are still used by an assembly or drawing (from the index) and returns the rest.
    /// Files selected together don't count: an assembly and the parts only it uses can go together.
    /// </summary>
    private List<FileRowViewModel> WithoutUsedFiles(List<FileRowViewModel> rows, string title)
    {
        bool Selected(string path) => rows.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        var used = rows.Select(r => (Row: r, Users: r.File.UsedIn.Where(u => !Selected(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .Where(x => x.Users.Count > 0).ToList();
        if (used.Count == 0) return rows;
        var rest = rows.Except(used.Select(x => x.Row)).ToList();
        _dialogs.ShowMessage(title,
            "Only files that nothing else uses can be deleted. These are still used:\n\n" +
            string.Join("\n", used.Select(x => $"\u2022 {x.Row.Name} is used in {PathList.Format(x.Users)}")) +
            "\n\nRemove them from those assemblies or drawings and check those in first (or select those files too, to delete them together)." +
            (rest.Count > 0 ? $"\n\nContinuing with {PathList.Format(rest.Select(r => r.Path).ToList())}." : ""),
            isWarning: true);
        return rest;
    }

    private Task RequestDeleteAsync()
    {
        var rows = WithoutUsedFiles(RequestDeleteTargets(), "Request Delete");
        if (rows.Count == 0) return Task.CompletedTask;
        var request = _dialogs.AskCheckIn("Request Delete", "Send Request", rows.Select(r => r.Path).ToList(), "", allowKeepCheckedOut: false,
            prompt: "Why should it be deleted? The admins see this when they decide.",
            footnote: "Nothing is deleted yet: the file stays in the vault until an admin deletes it.");
        if (request == null) return Task.CompletedTask;
        return RunAsync("Sending the delete request\u2026", async (vault, progress) =>
            Report(await vault.RequestDeleteAsync(rows.Select(r => r.Path), request.Comment, progress), "Asked to delete"));
    }

    private Task WithdrawDeleteRequestAsync()
    {
        var targets = WithdrawDeleteTargets().Select(r => r.Path).ToList();
        if (!_dialogs.Confirm("Withdraw Delete Request", $"Take back the delete request for {PathList.Format(targets)}?")) return Task.CompletedTask;
        return RunAsync("Withdrawing\u2026", async (vault, progress) =>
            Report(await vault.WithdrawDeleteRequestAsync(targets, progress), "Withdrew the delete request for"));
    }

    private Task RejectDeleteRequestAsync()
    {
        var rows = _selected.Where(r => r.File.DeleteRequest != null).ToList();
        var asked = string.Join("\n", rows.Select(r => $"\u2022 {r.Name}: {r.File.DeleteRequestedBy} asked: \u201C{r.File.DeleteRequest!.Reason}\u201D"));
        var request = _dialogs.AskCheckIn("Reject Delete Request (admin)", "Keep the file", rows.Select(r => r.Path).ToList(), "", allowKeepCheckedOut: false,
            prompt: "Why keep it? This is shown to the team in Discord.", footnote: asked);
        if (request == null) return Task.CompletedTask;
        return RunAsync("Rejecting\u2026", async (vault, progress) =>
            Report(await vault.RejectDeleteRequestAsync(rows.Select(r => r.Path), request.Comment, progress), "Kept"));
    }

    private Task DeleteFromVaultAsync()
    {
        var rows = WithoutUsedFiles(DeleteFromVaultTargets(), "Delete from Vault");
        if (rows.Count == 0) return Task.CompletedTask;
        var reasons = rows.Where(r => r.File.DeleteRequest != null)
            .Select(r => $"\u2022 {r.Name}: {r.File.DeleteRequestedBy} asked: \u201C{r.File.DeleteRequest!.Reason}\u201D").ToList();
        var defaultComment = rows.Count == 1 && rows[0].File.DeleteRequest is { } only
            ? $"Deleted {rows[0].Name} ({only.Reason})"
            : $"Deleted {PathList.Format(rows.Select(r => r.Path).ToList())}";
        var footnote = string.Join("\n", reasons) + (reasons.Count > 0 ? "\n\n" : "") +
                       "It's deleted for everyone at their next Get Latest. Older versions stay in the vault's history.";
        var request = _dialogs.AskCheckIn("Delete from Vault (admin)", "Delete for Everyone", rows.Select(r => r.Path).ToList(), defaultComment,
            allowKeepCheckedOut: false, prompt: "Check-in comment (shown in History and Discord):", footnote: footnote);
        if (request == null) return Task.CompletedTask;
        return RunAsync("Deleting from the vault\u2026", async (vault, progress) =>
            Report(await vault.DeleteFromVaultAsync(rows.Select(r => r.Path), request.Comment, progress), "Deleted from the vault"));
    }
}
