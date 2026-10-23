using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Errors;
using Jarvis.Core.Notifications;

namespace Jarvis.Core.Vault;

/// <summary>
/// Deleting files from the vault: teammates ask (with a reason) and admins see the requests; an admin
/// deletes a file for everyone (it leaves the vault at everyone's next Get Latest) or rejects the request.
/// </summary>
public sealed partial class VaultService
{
    /// <summary>The shortest reason accepted: a word or two isn't enough for an admin to decide.</summary>
    public const int MinimumDeleteReason = 10;

    /// <summary>Asks the admins to delete files from the vault. The reason is shown to them in Jarvis and in Discord.</summary>
    public async Task<OperationResult> RequestDeleteAsync(IEnumerable<string> paths, string reason, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var relatives = Normalize(paths);
        reason = (reason ?? "").Trim();
        if (reason.Length < MinimumDeleteReason)
        {
            throw new PdmException("Write why it should be deleted (for example \"replaced by bracket v2, nothing uses it\"), so an admin can decide.");
        }
        var login = GitHubLogin ?? throw new PdmException("Jarvis couldn't confirm your GitHub account, so the request can't be sent. Check that you're online and signed in to GitHub, then try again.");
        var snapshot = await GetSnapshotAsync(contactVault: false, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            await SyncCoreAsync(progress, ct).ConfigureAwait(false);
            var list = DeleteRequestList.Parse(await ReadVaultTextAsync(DeleteRequestList.RelativePath, ct).ConfigureAwait(false));
            foreach (var rel in relatives)
            {
                var file = snapshot.Files.FirstOrDefault(f => SamePath(f.RelativePath, rel));
                if (file == null || file.State == FileState.NotInVault)
                {
                    result.Fail(rel, new PdmException($"{PathList.Name(rel)} isn't in the vault (it was never checked in). Right-click it and choose Delete Local File instead."));
                }
                else if (list.Find(rel) is { } existing)
                {
                    result.Fail(rel, new PdmException($"{PathList.Name(rel)} already has a delete request from {Config.DisplayNameFor(existing.RequestedBy)}."));
                }
                else
                {
                    list.Requests.Add(new DeleteRequest { Path = rel, RequestedBy = login, RequestedAt = DateTimeOffset.Now, Reason = reason });
                    result.Succeeded.Add(rel);
                }
            }
            if (result.Succeeded.Count == 0) return result;

            WriteVaultText(DeleteRequestList.RelativePath, list.Serialize());
            await CommitAndPushAsync(new[] { DeleteRequestList.RelativePath }, $"Asked to delete {PathList.Format(result.Succeeded)}", progress, ct).ConfigureAwait(false);
            result.Notes.Add("The admins see the request in Jarvis. The file stays in the vault until an admin deletes it.");
            await NotifyAsync(new VaultEvent(VaultEventKind.DeleteRequested, UserDisplayName, result.Succeeded) { Comment = reason }, result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Takes back your own delete requests (admins may take back anyone's: that's rejecting it).</summary>
    public Task<OperationResult> WithdrawDeleteRequestAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default) =>
        RemoveDeleteRequestsAsync(paths, rejectNote: null, progress, ct);

    /// <summary>Admin: says no to delete requests; the file stays. The note is shown in Discord.</summary>
    public Task<OperationResult> RejectDeleteRequestAsync(IEnumerable<string> paths, string note, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        RequireAdmin("reject delete requests");
        return RemoveDeleteRequestsAsync(paths, (note ?? "").Trim(), progress, ct);
    }

    private async Task<OperationResult> RemoveDeleteRequestsAsync(IEnumerable<string> paths, string? rejectNote, IProgress<string>? progress, CancellationToken ct)
    {
        var relatives = Normalize(paths);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            await SyncCoreAsync(progress, ct).ConfigureAwait(false);
            var list = DeleteRequestList.Parse(await ReadVaultTextAsync(DeleteRequestList.RelativePath, ct).ConfigureAwait(false));
            var requesters = new List<string>();
            foreach (var rel in relatives)
            {
                var request = list.Find(rel);
                if (request == null)
                {
                    result.Fail(rel, new PdmException($"{PathList.Name(rel)} has no delete request."));
                }
                else if (rejectNote == null && !IsAdmin && !string.Equals(request.RequestedBy, GitHubLogin, StringComparison.OrdinalIgnoreCase))
                {
                    result.Fail(rel, new PdmException($"{PathList.Name(rel)} was requested by {Config.DisplayNameFor(request.RequestedBy)}. Only they or an admin can take the request back."));
                }
                else
                {
                    list.Requests.Remove(request);
                    requesters.Add(Config.DisplayNameFor(request.RequestedBy));
                    result.Succeeded.Add(rel);
                }
            }
            if (result.Succeeded.Count == 0) return result;

            WriteVaultText(DeleteRequestList.RelativePath, list.Serialize());
            var names = PathList.Format(result.Succeeded);
            await CommitAndPushAsync(new[] { DeleteRequestList.RelativePath },
                rejectNote == null ? $"Withdrew the delete request for {names}" : $"Rejected the delete request for {names}", progress, ct).ConfigureAwait(false);
            if (rejectNote != null)
            {
                await NotifyAsync(new VaultEvent(VaultEventKind.DeleteRejected, UserDisplayName, result.Succeeded)
                {
                    Comment = rejectNote,
                    PreviousOwner = string.Join(", ", requesters.Distinct()),
                }, result).ConfigureAwait(false);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Admin: deletes files from the vault for everyone, in one check-in (with their index entries and
    /// 3D previews); teammates' copies go away at their next Get Latest. Older versions stay in the vault's
    /// history. Any delete requests for them are closed.
    /// </summary>
    public async Task<OperationResult> DeleteFromVaultAsync(IEnumerable<string> paths, string comment, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        RequireAdmin("delete files from the vault");
        var relatives = Normalize(paths);
        var result = new OperationResult();
        var snapshot = await GetSnapshotAsync(contactVault: true, ct).ConfigureAwait(false);

        var targets = new List<VaultFile>();
        foreach (var rel in relatives)
        {
            var file = snapshot.Files.FirstOrDefault(f => SamePath(f.RelativePath, rel));
            var name = PathList.Name(rel);
            var problem = file?.State switch
            {
                null or FileState.NotInVault => $"{name} isn't in the vault (it was never checked in). Use Delete Local File instead.",
                FileState.CheckedOutByOther => $"{name} is checked out by {file.CheckedOutBy}. Ask them to check it in (or use Break Lock), then delete it.",
                FileState.Frozen => $"{name} is frozen. Unfreeze it first.",
                FileState.InReview => $"{name} is in review. Approve or reject the review first.",
                _ => null,
            };
            if (problem != null) result.Fail(rel, new PdmException(problem));
            else targets.Add(file!);
        }
        if (targets.Count == 0) return result;

        // Deleting a lockable file needs its lock, like any other change to it.
        var toLock = targets.Where(f => f.IsLockable && f.State != FileState.CheckedOutByMe).Select(f => f.RelativePath).ToList();
        if (toLock.Count > 0)
        {
            var locked = await CheckOutAsync(toLock, progress, ct).ConfigureAwait(false);
            foreach (var failure in locked.Failures)
            {
                result.Failures.Add(failure);
                targets.RemoveAll(f => SamePath(f.RelativePath, failure.Path));
            }
        }
        if (targets.Count == 0) return result;

        progress?.Report("Deleting\u2026");
        var indexUpdates = new Dictionary<string, IndexEntry?>(StringComparer.OrdinalIgnoreCase);
        var previews = new List<string>();
        foreach (var file in targets)
        {
            var full = ToFullPath(file.RelativePath);
            if (File.Exists(full))
            {
                FileUtil.SetReadOnly(full, false);
                File.Delete(full);
            }
            if (file.Index != null) indexUpdates[file.RelativePath] = null;
            var preview = VaultPaths.PreviewFor(file.RelativePath);
            if (File.Exists(ToFullPath(preview))) File.Delete(ToFullPath(preview));
            previews.Add(preview);
        }

        var deleted = targets.Select(f => f.RelativePath).ToList();
        var message = string.IsNullOrWhiteSpace(comment) ? $"Deleted {PathList.Format(deleted)} from the vault" : comment.Trim();
        OperationResult checkIn;
        try
        {
            checkIn = await CheckInAsync(deleted, message, progress: progress, ct: ct, indexUpdates: indexUpdates, previews: previews).ConfigureAwait(false);
        }
        catch (PdmException ex)
        {
            throw new PdmException(ex.Message + "\n\nThe files are deleted on this PC but not from the vault yet. Use Undo Check Out on them to bring them back, " +
                                   "or try Delete from Vault again.", ex.Detail, ex);
        }
        result.Succeeded.AddRange(checkIn.Succeeded);
        result.Failures.AddRange(checkIn.Failures);
        result.Warnings.AddRange(checkIn.Warnings);

        // Close any delete requests for them (a separate small change, so the delete itself never waits on it).
        var withRequests = deleted.Where(d => snapshot.Files.Any(f => SamePath(f.RelativePath, d) && f.DeleteRequest != null)).ToList();
        if (withRequests.Count > 0)
        {
            try
            {
                await RemoveDeleteRequestsAsync(withRequests, rejectNote: null, progress, ct).ConfigureAwait(false);
            }
            catch (PdmException ex)
            {
                result.Warnings.Add("The files are deleted, but their delete requests couldn't be cleared: " + ex.Message);
            }
        }
        result.Notes.Add("Teammates' copies go away at their next Get Latest. Older versions are still in the vault's history (History, or a milestone).");
        await NotifyAsync(new VaultEvent(VaultEventKind.DeletedFromVault, UserDisplayName, deleted) { Comment = comment?.Trim() }, result).ConfigureAwait(false);
        return result;
    }
}
