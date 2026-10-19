using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CapstonePdm.Core.Config;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Git;
using CapstonePdm.Core.Notifications;

namespace CapstonePdm.Core.Vault;

/// <summary>
/// PDM commands on top of Git + Git LFS locking.
///
/// Ground rules that keep teammates' work safe:
///  * Get Latest only fast-forwards or merges. Files with local changes are never touched; if the
///    vault changed one of them, Get Latest stops and reports a conflict.
///  * Nothing rebases or stashes, so files SolidWorks has open are never rewritten underneath it.
///  * A failed push leaves the commit and the lock in place, so retrying Check In finishes the job.
///
/// Commands are serialized (one Git operation at a time). All awaits use ConfigureAwait(false)
/// so the library is safe to call from SolidWorks' UI thread in Phase 2.
/// </summary>
public sealed class VaultService : IVaultService
{
    private const string LoginConfigKey = "pdm.githubLogin";
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(60);

    private readonly IGitRunner _git;
    private readonly INotifier _notifier;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private IReadOnlyList<LockInfo> _lastLocks = Array.Empty<LockInfo>();
    private bool _locksKnown;
    private string? _configProblem;

    private VaultService(string vaultRoot, IGitRunner git, INotifier? notifier)
    {
        VaultRoot = vaultRoot;
        _git = git;
        Config = new VaultConfig();
        ReloadConfig();
        _notifier = notifier ?? new DiscordWebhookNotifier(() => Config);
    }

    public string VaultRoot { get; }
    public VaultConfig Config { get; private set; }
    public string UserName { get; private set; } = Environment.UserName;
    public string? GitHubLogin { get; private set; }
    public bool IsAdmin => Config.IsAdmin(GitHubLogin);

    public string UserDisplayName =>
        GitHubLogin != null && Config.Team.TryGetValue(GitHubLogin, out var name) && !string.IsNullOrWhiteSpace(name) ? name : UserName;

    private string Upstream => Config.Remote + "/" + Config.Branch;

    public static async Task<VaultService> OpenAsync(string vaultRoot, INotifier? notifier = null, IGitRunner? git = null, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(vaultRoot).TrimEnd('\\', '/');
        var dotGit = Path.Combine(root, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit)) throw new VaultNotFoundException(root);

        if (git == null)
        {
            var exe = GitRunner.FindGit() ?? throw new GitNotFoundException();
            git = new GitRunner(root, exe);
        }

        var service = new VaultService(root, git, notifier);
        await service.LoadIdentityAsync(ct).ConfigureAwait(false);
        return service;
    }

    // ---------------------------------------------------------------- paths

    public string ToRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is empty.", nameof(path));

        string relative;
        if (Path.IsPathRooted(path))
        {
            var full = Path.GetFullPath(path);
            var prefix = VaultRoot + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new PdmException($"{path} isn't inside the vault folder ({VaultRoot}). Only files in the vault can be checked out or in.");
            }
            relative = full.Substring(prefix.Length);
        }
        else
        {
            relative = path;
        }
        return relative.Replace('\\', '/').TrimStart('/');
    }

    public string ToFullPath(string relativePath) =>
        Path.Combine(VaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private List<string> Normalize(IEnumerable<string> paths)
    {
        var list = paths.Select(ToRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) throw new PdmException("Select at least one file first.");
        return list;
    }

    // ---------------------------------------------------------------- identity

    private async Task LoadIdentityAsync(CancellationToken ct)
    {
        var name = await GetConfigValueAsync("user.name", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(name)) UserName = name!;
        GitHubLogin = await GetConfigValueAsync(LoginConfigKey, ct).ConfigureAwait(false);
    }

    public async Task SetGitHubLoginAsync(string login, CancellationToken ct = default)
    {
        login = login.Trim();
        if (login.Length == 0) return;
        await RunCheckedAsync("save your GitHub username", new[] { "config", "--local", LoginConfigKey, login }, ct).ConfigureAwait(false);
        GitHubLogin = login;
    }

    private async Task<string?> GetConfigValueAsync(string key, CancellationToken ct)
    {
        var r = await RunAsync(new[] { "config", "--get", key }, ct).ConfigureAwait(false);
        var value = r.StdOut.Trim();
        return r.Success && value.Length > 0 ? value : null;
    }

    private void ReloadConfig()
    {
        try
        {
            Config = VaultConfig.Load(VaultRoot);
            _configProblem = null;
        }
        catch (PdmException ex)
        {
            _configProblem = ex.Message;
        }
    }

    // ---------------------------------------------------------------- snapshot (vault view)

    public async Task<VaultSnapshot> GetSnapshotAsync(bool contactVault = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await GetSnapshotCoreAsync(contactVault, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VaultFile?> GetFileAsync(string path, bool contactVault = false, CancellationToken ct = default)
    {
        var relative = ToRelativePath(path);
        var snapshot = await GetSnapshotAsync(contactVault, ct).ConfigureAwait(false);
        return snapshot.Files.FirstOrDefault(f => SamePath(f.RelativePath, relative));
    }

    private async Task<VaultSnapshot> GetSnapshotCoreAsync(bool contactVault, CancellationToken ct)
    {
        ReloadConfig();
        var snapshot = new VaultSnapshot { ConfigProblem = _configProblem };

        if (contactVault)
        {
            try
            {
                await FetchAsync(ct).ConfigureAwait(false);
                await GetLocksAsync(ct).ConfigureAwait(false);
            }
            catch (PdmException ex)
            {
                snapshot.IsOffline = ex is VaultOfflineException || ex is VaultAuthException;
                snapshot.ConnectionProblem = ex.Message;
            }
        }
        snapshot.LocksUnknown = !_locksKnown;

        var tracked = GitParsers.SplitZ((await RunCheckedAsync("list the vault files", new[] { "ls-files", "-z" }, ct).ConfigureAwait(false)).StdOut);
        var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));
        var incoming = await GetIncomingChangesAsync(ct).ConfigureAwait(false);
        var latest = await GetLatestVersionsAsync(ct).ConfigureAwait(false);
        var locks = _lastLocks;

        if (await UpstreamExistsAsync(ct).ConfigureAwait(false))
        {
            snapshot.PendingCheckIns = await CountCommitsAsync(Upstream + "..HEAD", ct).ConfigureAwait(false);
            snapshot.UpdatesAvailable = await CountCommitsAsync("HEAD.." + Upstream, ct).ConfigureAwait(false);
        }

        var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        all.UnionWith(tracked);
        all.UnionWith(changes.Values.Where(c => c.IsUntracked).Select(c => c.Path));
        all.UnionWith(locks.Select(l => l.Path));
        all.UnionWith(incoming);
        all.RemoveWhere(IsHidden);

        var lockable = await GetLockableAsync(all, ct).ConfigureAwait(false);
        var files = new List<VaultFile>(all.Count);
        foreach (var path in all)
        {
            changes.TryGetValue(path, out var change);
            var fileLock = FindLock(locks, path);
            var isLockable = lockable.Contains(path);

            FileState state;
            if (fileLock != null) state = fileLock.IsMine ? FileState.CheckedOutByMe : FileState.CheckedOutByOther;
            else if (change == null) state = FileState.Available;
            else if (change.IsUntracked) state = FileState.NotInVault;
            else if (change.IsDeleted) state = FileState.MissingLocally;
            else state = isLockable ? FileState.ModifiedWithoutCheckOut : FileState.Modified;

            latest.TryGetValue(path, out var version);
            files.Add(new VaultFile
            {
                RelativePath = path,
                State = state,
                IsLockable = isLockable,
                Lock = fileLock,
                CheckedOutBy = fileLock == null ? null : fileLock.IsMine ? UserDisplayName : Config.DisplayNameFor(fileLock.Owner),
                HasLocalChanges = change != null,
                IsOutdated = incoming.Contains(path),
                ExistsLocally = File.Exists(ToFullPath(path)),
                LatestVersion = version,
            });
        }

        snapshot.Files = files;
        snapshot.RetrievedAt = DateTimeOffset.Now;
        return snapshot;
    }

    private static bool IsHidden(string path) =>
        path.Split('/').Any(segment => segment.StartsWith(".", StringComparison.Ordinal));

    // ---------------------------------------------------------------- Get Latest

    public async Task<GetLatestResult> GetLatestAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await SyncCoreAsync(progress, ct).ConfigureAwait(false);
            ReloadConfig();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings this PC up to date with the vault without touching any file that has local changes.
    /// </summary>
    private async Task<GetLatestResult> SyncCoreAsync(IProgress<string>? progress, CancellationToken ct, bool fetch = true)
    {
        if (fetch)
        {
            progress?.Report("Contacting the vault…");
            await FetchAsync(ct).ConfigureAwait(false);
        }
        if (!await UpstreamExistsAsync(ct).ConfigureAwait(false)) return GetLatestResult.UpToDate;

        var behind = await CountCommitsAsync("HEAD.." + Upstream, ct).ConfigureAwait(false);
        if (behind == 0) return GetLatestResult.UpToDate;

        var incoming = await GetIncomingChangesAsync(ct).ConfigureAwait(false);
        var local = await GetLocalChangesAsync(ct).ConfigureAwait(false);

        // The promise: never overwrite a file you've changed. Git would refuse anyway, but we
        // check first so the message is clear and nothing is half-done.
        var conflicts = local.Select(c => c.Path).Where(incoming.Contains).ToList();
        if (conflicts.Count > 0)
        {
            throw new VaultConflictException(conflicts,
                "These files have a newer version in the vault, but you also changed them on this PC:\n  " +
                string.Join("\n  ", conflicts) +
                "\n\nNothing was overwritten. This usually means someone broke your check-out, or the file was edited without checking it out. " +
                "Save a copy of your version somewhere outside the vault if you need it, then use Undo Check Out on these files and Get Latest again.");
        }

        var inUse = incoming.Where(p => FileUtil.IsInUse(ToFullPath(p))).ToList();
        if (inUse.Count > 0)
        {
            throw new FileInUseException(inUse, "The vault has a newer version. Close them in SolidWorks so they can be updated, then try again.");
        }

        progress?.Report($"Getting {incoming.Count} updated file(s) from the vault…");
        var merge = await RunAsync(new[] { "merge", "--ff", "--no-edit", "--no-stat", "-m", "Merge vault changes", Upstream }, ct, TransferTimeout).ConfigureAwait(false);
        if (!merge.Success)
        {
            if (File.Exists(Path.Combine(VaultRoot, ".git", "MERGE_HEAD")))
            {
                await RunAsync(new[] { "merge", "--abort" }, ct).ConfigureAwait(false);
            }
            throw GitErrorClassifier.Classify(merge, "get the latest versions");
        }

        return new GetLatestResult(behind, incoming.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
    }

    // ---------------------------------------------------------------- Check Out

    public async Task<OperationResult> CheckOutAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var relatives = Normalize(paths);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CheckOutCoreAsync(relatives, true, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OperationResult> CheckOutCoreAsync(IReadOnlyList<string> relatives, bool notify, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new OperationResult();
        var lockable = await GetLockableAsync(relatives, ct).ConfigureAwait(false);
        var targets = new List<string>();
        foreach (var rel in relatives)
        {
            if (lockable.Contains(rel)) targets.Add(rel);
            else result.Fail(rel, new PdmException($"{PathList.Name(rel)} isn't a lockable file type, so it doesn't need checking out. Just edit it and check it in."));
        }
        if (targets.Count == 0) return result;

        // 1. Latest version first, so nobody edits a stale copy.
        try
        {
            await SyncCoreAsync(progress, ct).ConfigureAwait(false);
        }
        catch (VaultOfflineException)
        {
            throw;
        }
        catch (VaultAuthException)
        {
            throw;
        }
        catch (PdmException ex)
        {
            result.Warnings.Add("Get Latest didn't finish, so some other files may be out of date: " + ex.Message);
        }
        var stillOutdated = await GetIncomingChangesAsync(ct).ConfigureAwait(false);

        // 2. Lock.
        progress?.Report("Checking out…");
        var locks = await GetLocksAsync(ct).ConfigureAwait(false);
        foreach (var rel in targets)
        {
            ct.ThrowIfCancellationRequested();

            if (stillOutdated.Contains(rel))
            {
                result.Fail(rel, new VaultConflictException(new[] { rel },
                    $"A newer version of {PathList.Name(rel)} is in the vault but couldn't be downloaded (see the other messages). Fix that, then check out again."));
                continue;
            }

            var existing = FindLock(locks, rel);
            if (existing != null)
            {
                if (existing.IsMine)
                {
                    FileUtil.SetReadOnly(ToFullPath(rel), false);
                    result.Succeeded.Add(rel);
                }
                else
                {
                    result.Fail(rel, new LockHeldException(rel, Config.DisplayNameFor(existing.Owner), existing.LockedAt));
                }
                continue;
            }

            var lockResult = await RunAsync(new[] { "lfs", "lock", "--json", rel }, ct, NetworkTimeout).ConfigureAwait(false);
            if (!lockResult.Success)
            {
                var error = GitErrorClassifier.Classify(lockResult, "check out " + PathList.Name(rel));
                if (IsLockExistsError(lockResult))
                {
                    // Someone locked it between our lock query and now.
                    var holder = FindLock(await GetLocksAsync(ct).ConfigureAwait(false), rel);
                    if (holder != null && holder.IsMine)
                    {
                        FileUtil.SetReadOnly(ToFullPath(rel), false);
                        result.Succeeded.Add(rel);
                        continue;
                    }
                    if (holder != null) error = new LockHeldException(rel, Config.DisplayNameFor(holder.Owner), holder.LockedAt);
                }
                result.Fail(rel, error);
                continue;
            }

            FileUtil.SetReadOnly(ToFullPath(rel), false);
            result.Succeeded.Add(rel);
        }

        if (result.Succeeded.Count == 0) return result;

        // 3. Someone may have checked in between our Get Latest and our lock. Now that we hold the
        //    lock nobody else can, so one more look settles it.
        try
        {
            await FetchAsync(ct).ConfigureAwait(false);
            var incoming = await GetIncomingChangesAsync(ct).ConfigureAwait(false);
            if (result.Succeeded.Any(incoming.Contains)) await SyncCoreAsync(progress, ct, fetch: false).ConfigureAwait(false);
        }
        catch (PdmException ex)
        {
            result.Warnings.Add("Couldn't confirm you have the newest version: " + ex.Message + " Run Get Latest before editing.");
        }
        foreach (var rel in result.Succeeded) FileUtil.SetReadOnly(ToFullPath(rel), false);

        if (notify)
        {
            await NotifyAsync(new VaultEvent(VaultEventKind.CheckedOut, UserDisplayName, result.Succeeded.ToList()), result).ConfigureAwait(false);
        }
        return result;
    }

    private static bool IsLockExistsError(GitResult r)
    {
        var text = r.StdErr + r.StdOut;
        return text.IndexOf("already created lock", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("lock exists", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("already locked", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ---------------------------------------------------------------- Check In

    public async Task<OperationResult> CheckInAsync(IEnumerable<string> paths, string comment, bool keepCheckedOut = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var relatives = Normalize(paths);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CheckInCoreAsync(relatives, comment, keepCheckedOut, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OperationResult> CheckInCoreAsync(IReadOnlyList<string> relatives, string comment, bool keepCheckedOut, IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new PdmException("Enter a check-in comment that says what changed, for example \"added mounting holes\".");
        }
        comment = comment.Trim();
        var result = new OperationResult();

        var lockable = await GetLockableAsync(relatives, ct).ConfigureAwait(false);
        var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));

        // Lockable files must be checked out to you (brand-new files excepted: nobody else has them).
        IReadOnlyList<LockInfo> locks = Array.Empty<LockInfo>();
        if (relatives.Any(lockable.Contains))
        {
            progress?.Report("Checking check-outs…");
            locks = await GetLocksAsync(ct).ConfigureAwait(false);
        }

        var notCheckedOut = new List<string>();
        foreach (var rel in relatives.Where(lockable.Contains))
        {
            var fileLock = FindLock(locks, rel);
            if (fileLock != null && !fileLock.IsMine) throw new LockHeldException(rel, Config.DisplayNameFor(fileLock.Owner), fileLock.LockedAt);
            var isNew = changes.TryGetValue(rel, out var change) && change.IsUntracked;
            if (fileLock == null && !isNew) notCheckedOut.Add(rel);
        }
        if (notCheckedOut.Count > 0) throw new NotCheckedOutException(notCheckedOut);

        var inUse = relatives.Where(r => FileUtil.IsInUse(ToFullPath(r))).ToList();
        if (inUse.Count > 0) throw new FileInUseException(inUse, "Save and close them in SolidWorks, then check in again.");

        var changed = relatives.Where(changes.ContainsKey).ToList();

        // Commit on top of the newest vault state, so the push is a fast-forward.
        await SyncCoreAsync(progress, ct).ConfigureAwait(false);

        if (changed.Count > 0)
        {
            progress?.Report("Saving the new version…");
            var pathspec = string.Join("\0", changed) + "\0";
            await RunCheckedAsync("check in", new[] { "add", "--all", "--pathspec-from-file=-", "--pathspec-file-nul" }, ct, TransferTimeout, pathspec).ConfigureAwait(false);
            await RunCheckedAsync("check in", new[] { "commit", "--quiet", "-m", comment, "--pathspec-from-file=-", "--pathspec-file-nul" }, ct, null, pathspec).ConfigureAwait(false);
        }

        // Pushes this check-in plus any earlier one whose push failed.
        var ahead = await UpstreamExistsAsync(ct).ConfigureAwait(false)
            ? await CountCommitsAsync(Upstream + "..HEAD", ct).ConfigureAwait(false)
            : 1;
        if (ahead > 0)
        {
            progress?.Report("Sending to the vault…");
            try
            {
                await PushWithRetryAsync(progress, ct).ConfigureAwait(false);
            }
            catch (PdmException ex)
            {
                throw PushRejectedException.NotSent(ex);
            }
        }
        else if (changed.Count == 0)
        {
            result.Notes.Add("No changes were found, so no new version was made.");
        }
        result.Succeeded.AddRange(relatives);

        if (!keepCheckedOut)
        {
            progress?.Report("Releasing check-outs…");
            foreach (var rel in relatives.Where(lockable.Contains))
            {
                if (FindLock(locks, rel) == null) continue; // brand-new file, never locked
                var unlock = await RunAsync(new[] { "lfs", "unlock", rel }, ct, NetworkTimeout).ConfigureAwait(false);
                if (unlock.Success)
                {
                    FileUtil.SetReadOnly(ToFullPath(rel), true);
                }
                else
                {
                    var why = GitErrorClassifier.Classify(unlock, "release the check-out").Message;
                    result.Warnings.Add($"{PathList.Name(rel)} was checked in, but it's still checked out to you ({why}). Use Undo Check Out to release it.");
                }
            }
        }

        if (changed.Count > 0 || ahead > 0)
        {
            var noticeFiles = changed.Count > 0 ? changed : relatives.ToList();
            var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in noticeFiles)
            {
                versions[rel] = await CountCommitsAsync("HEAD", ct, "--no-merges", rel).ConfigureAwait(false);
            }
            var e = new VaultEvent(VaultEventKind.CheckedIn, UserDisplayName, noticeFiles)
            {
                Comment = comment,
                Versions = versions,
                KeptCheckedOut = keepCheckedOut,
            };
            await NotifyAsync(e, result).ConfigureAwait(false);
        }
        else if (!keepCheckedOut && relatives.Any(r => FindLock(locks, r) != null))
        {
            await NotifyAsync(new VaultEvent(VaultEventKind.UndoCheckOut, UserDisplayName, relatives.ToList()), result).ConfigureAwait(false);
        }

        return result;
    }

    private async Task PushWithRetryAsync(IProgress<string>? progress, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var push = await RunAsync(new[] { "push", Config.Remote, "HEAD:refs/heads/" + Config.Branch }, ct, TransferTimeout).ConfigureAwait(false);
            if (push.Success) return;

            var error = GitErrorClassifier.Classify(push, "check in");
            if (error is PushRejectedException rejected && rejected.Reason == PushRejectReason.OutOfDate && attempt < 3)
            {
                progress?.Report("Someone checked in at the same moment. Merging and retrying…");
                await SyncCoreAsync(progress, ct).ConfigureAwait(false);
                continue;
            }
            throw error;
        }
    }

    // ---------------------------------------------------------------- Undo Check Out

    public async Task<OperationResult> UndoCheckOutAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var relatives = Normalize(paths);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await UndoCheckOutCoreAsync(relatives, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OperationResult> UndoCheckOutCoreAsync(IReadOnlyList<string> relatives, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new OperationResult();
        var lockable = await GetLockableAsync(relatives, ct).ConfigureAwait(false);
        var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));
        var locks = relatives.Any(lockable.Contains) ? await GetLocksAsync(ct).ConfigureAwait(false) : Array.Empty<LockInfo>();

        var inUse = relatives.Where(r => changes.ContainsKey(r) && FileUtil.IsInUse(ToFullPath(r))).ToList();
        if (inUse.Count > 0) throw new FileInUseException(inUse, "Close them in SolidWorks (without saving), then try again.");

        progress?.Report("Undoing check-out…");
        string? backupFolder = null;
        var released = new List<string>();
        foreach (var rel in relatives)
        {
            var fileLock = FindLock(locks, rel);
            changes.TryGetValue(rel, out var change);

            if (fileLock != null && !fileLock.IsMine)
            {
                result.Fail(rel, new PdmException($"{PathList.Name(rel)} is checked out by {Config.DisplayNameFor(fileLock.Owner)}, not you, so there's nothing to undo."));
                continue;
            }
            if (change != null && change.IsUntracked)
            {
                result.Fail(rel, new PdmException($"{PathList.Name(rel)} is new and has never been checked in, so there's no vault version to go back to. Delete it yourself if you don't want it."));
                continue;
            }
            if (fileLock == null && change == null)
            {
                result.Fail(rel, new PdmException($"{PathList.Name(rel)} isn't checked out to you and has no changes, so there's nothing to undo."));
                continue;
            }

            if (change != null)
            {
                backupFolder ??= FileUtil.NewBackupFolder();
                FileUtil.Backup(ToFullPath(rel), backupFolder, rel);
                var restore = await RunAsync(new[] { "checkout", "HEAD", "--", rel }, ct, TransferTimeout).ConfigureAwait(false);
                if (!restore.Success)
                {
                    result.Fail(rel, GitErrorClassifier.Classify(restore, "undo the check-out"));
                    continue;
                }
            }

            if (fileLock != null)
            {
                var unlock = await RunAsync(new[] { "lfs", "unlock", rel }, ct, NetworkTimeout).ConfigureAwait(false);
                if (!unlock.Success)
                {
                    result.Fail(rel, GitErrorClassifier.Classify(unlock, "release the check-out"));
                    continue;
                }
                released.Add(rel);
            }

            if (lockable.Contains(rel)) FileUtil.SetReadOnly(ToFullPath(rel), true);
            result.Succeeded.Add(rel);
        }

        if (backupFolder != null && Directory.Exists(backupFolder))
        {
            result.Notes.Add($"Your discarded changes were backed up to {backupFolder}, just in case.");
        }
        if (released.Count > 0)
        {
            await NotifyAsync(new VaultEvent(VaultEventKind.UndoCheckOut, UserDisplayName, released), result).ConfigureAwait(false);
        }
        return result;
    }

    // ---------------------------------------------------------------- History and restore

    public async Task<IReadOnlyList<FileVersion>> GetHistoryAsync(string path, CancellationToken ct = default)
    {
        var rel = ToRelativePath(path);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Include vault versions this PC hasn't got yet.
            var args = new List<string> { "log", "--no-merges", "--format=" + GitParsers.HeaderFormat + "%B", "HEAD" };
            if (await UpstreamExistsAsync(ct).ConfigureAwait(false)) args.Add(Upstream);
            args.Add("--");
            args.Add(rel);
            var log = await RunCheckedAsync("read the file history", args.ToArray(), ct).ConfigureAwait(false);
            return GitParsers.ParseHistory(log.StdOut);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks the file out if needed, puts the old version in place and checks it in as a new version.</summary>
    public async Task<OperationResult> RestoreVersionAsync(string path, FileVersion version, string comment, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var rel = ToRelativePath(path);
        if (string.IsNullOrWhiteSpace(comment)) throw new PdmException("Enter a check-in comment for the restored version.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var checkOut = await CheckOutCoreAsync(new[] { rel }, false, progress, ct).ConfigureAwait(false);
            if (checkOut.Failures.Count > 0) throw checkOut.Failures[0].Error;

            var full = ToFullPath(rel);
            if (FileUtil.IsInUse(full)) throw new FileInUseException(new[] { rel }, "Close it in SolidWorks, then restore again.");

            var notes = new List<string>();
            var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));
            if (changes.ContainsKey(rel))
            {
                var backupFolder = FileUtil.NewBackupFolder();
                FileUtil.Backup(full, backupFolder, rel);
                notes.Add($"Your unsaved changes to {PathList.Name(rel)} were backed up to {backupFolder}.");
            }

            progress?.Report($"Getting version {version.Version}…");
            await RunCheckedAsync("restore the old version", new[] { "checkout", version.CommitId, "--", rel }, ct, TransferTimeout).ConfigureAwait(false);
            FileUtil.SetReadOnly(full, false);

            var checkIn = await CheckInCoreAsync(new[] { rel }, comment, false, progress, ct).ConfigureAwait(false);
            checkIn.Warnings.InsertRange(0, checkOut.Warnings);
            checkIn.Notes.InsertRange(0, notes);
            return checkIn;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- Admin

    public async Task<OperationResult> BreakLockAsync(string path, CancellationToken ct = default)
    {
        var rel = ToRelativePath(path);
        if (!IsAdmin)
        {
            throw new PdmException("Only vault admins can break a check-out. Admins are listed in pdm.json; your GitHub username is " +
                                   (GitHubLogin ?? "not set (see Settings)") + ".");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            var fileLock = FindLock(await GetLocksAsync(ct).ConfigureAwait(false), rel)
                           ?? throw new PdmException($"{PathList.Name(rel)} isn't checked out by anyone.");

            await RunCheckedAsync("break the check-out", new[] { "lfs", "unlock", "--force", "--id", fileLock.Id }, ct, NetworkTimeout).ConfigureAwait(false);
            result.Succeeded.Add(rel);

            var e = new VaultEvent(VaultEventKind.LockBroken, UserDisplayName, new[] { rel })
            {
                PreviousOwner = Config.DisplayNameFor(fileLock.Owner),
            };
            await NotifyAsync(e, result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- Git helpers

    private async Task<GitResult> RunAsync(string[] args, CancellationToken ct, TimeSpan? timeout = null, string? stdIn = null) =>
        await _git.RunAsync(args, stdIn, timeout, ct).ConfigureAwait(false);

    private async Task<GitResult> RunCheckedAsync(string action, string[] args, CancellationToken ct, TimeSpan? timeout = null, string? stdIn = null)
    {
        var result = await RunAsync(args, ct, timeout, stdIn).ConfigureAwait(false);
        if (!result.Success) throw GitErrorClassifier.Classify(result, action);
        return result;
    }

    private Task FetchAsync(CancellationToken ct) =>
        RunCheckedAsync("contact the vault", new[] { "fetch", "--prune", "--no-tags", Config.Remote }, ct, NetworkTimeout);

    private async Task<bool> UpstreamExistsAsync(CancellationToken ct) =>
        (await RunAsync(new[] { "rev-parse", "--verify", "--quiet", Upstream + "^{commit}" }, ct).ConfigureAwait(false)).Success;

    private async Task<int> CountCommitsAsync(string range, CancellationToken ct, string? option = null, string? path = null)
    {
        var args = new List<string> { "rev-list", "--count" };
        if (option != null) args.Add(option);
        args.Add(range);
        if (path != null)
        {
            args.Add("--");
            args.Add(path);
        }
        var r = await RunAsync(args.ToArray(), ct).ConfigureAwait(false);
        return r.Success && int.TryParse(r.StdOut.Trim(), out var n) ? n : 0;
    }

    /// <summary>Files changed in the vault since this PC last got latest.</summary>
    private async Task<HashSet<string>> GetIncomingChangesAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!await UpstreamExistsAsync(ct).ConfigureAwait(false)) return set;
        var diff = await RunAsync(new[] { "diff", "--name-only", "-z", "--no-renames", "HEAD..." + Upstream }, ct).ConfigureAwait(false);
        if (diff.Success) set.UnionWith(GitParsers.SplitZ(diff.StdOut));
        return set;
    }

    private async Task<List<StatusEntry>> GetLocalChangesAsync(CancellationToken ct)
    {
        var status = await RunCheckedAsync("read local file status",
            new[] { "status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames" }, ct).ConfigureAwait(false);
        return GitParsers.ParseStatus(status.StdOut);
    }

    private static Dictionary<string, StatusEntry> ToMap(IEnumerable<StatusEntry> entries)
    {
        var map = new Dictionary<string, StatusEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) map[entry.Path] = entry;
        return map;
    }

    /// <summary>Which of these paths .gitattributes marks as lockable.</summary>
    private async Task<HashSet<string>> GetLockableAsync(IEnumerable<string> relatives, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = relatives.ToList();
        if (list.Count == 0) return set;

        var r = await RunCheckedAsync("read file types", new[] { "check-attr", "--stdin", "-z", "lockable" }, ct, null,
            string.Join("\0", list) + "\0").ConfigureAwait(false);
        foreach (var (path, value) in GitParsers.ParseCheckAttr(r.StdOut))
        {
            if (value == "set") set.Add(path);
        }
        return set;
    }

    private async Task<IReadOnlyList<LockInfo>> GetLocksAsync(CancellationToken ct)
    {
        // --verify splits locks into ours/theirs using the GitHub account, so we don't need to know the username.
        var verified = await RunAsync(new[] { "lfs", "locks", "--verify", "--json" }, ct, NetworkTimeout).ConfigureAwait(false);
        List<LockInfo> locks;
        if (verified.Success)
        {
            locks = GitParsers.ParseVerifiedLocks(verified.StdOut);
        }
        else
        {
            var plain = await RunAsync(new[] { "lfs", "locks", "--json" }, ct, NetworkTimeout).ConfigureAwait(false);
            if (!plain.Success) throw GitErrorClassifier.Classify(verified, "read who has files checked out");
            locks = GitParsers.ParseLocks(plain.StdOut, GitHubLogin);
        }

        // Learn the GitHub username from our own locks if Setup didn't record it.
        var mine = locks.FirstOrDefault(l => l.IsMine && l.Owner.Length > 0);
        if (mine != null && !string.Equals(mine.Owner, GitHubLogin, StringComparison.OrdinalIgnoreCase))
        {
            GitHubLogin = mine.Owner;
            await RunAsync(new[] { "config", "--local", LoginConfigKey, mine.Owner }, ct).ConfigureAwait(false);
        }

        _lastLocks = locks;
        _locksKnown = true;
        return locks;
    }

    private async Task<Dictionary<string, FileVersion>> GetLatestVersionsAsync(CancellationToken ct)
    {
        var log = await RunAsync(new[] { "log", "--no-merges", "--name-only", "--no-renames", "--format=" + GitParsers.HeaderFormat + "%s", "HEAD" }, ct).ConfigureAwait(false);
        return log.Success ? GitParsers.ParseLatestVersions(log.StdOut) : new Dictionary<string, FileVersion>();
    }

    private static LockInfo? FindLock(IEnumerable<LockInfo> locks, string relative) =>
        locks.FirstOrDefault(l => SamePath(l.Path, relative));

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private async Task NotifyAsync(VaultEvent e, OperationResult result)
    {
        try
        {
            await _notifier.NotifyAsync(e).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"The Discord notice wasn't sent ({ex.Message}). Let the team know yourself.");
        }
    }
}
