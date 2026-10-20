using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Config;
using Jarvis.Core.Errors;
using Jarvis.Core.Git;
using Jarvis.Core.Notifications;

namespace Jarvis.Core.Vault;

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
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(60);

    private readonly IGitRunner _git;
    private readonly INotifier _notifier;
    private readonly IIdentityProvider _identityProvider;
    private VaultIdentity? _identity;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private IReadOnlyList<LockInfo> _lastLocks = Array.Empty<LockInfo>();
    private bool _locksKnown;
    private string? _configProblem;

    private VaultService(string vaultRoot, IGitRunner git, INotifier? notifier, IIdentityProvider? identityProvider)
    {
        VaultRoot = vaultRoot;
        _git = git;
        _identityProvider = identityProvider ?? new GitHubIdentityProvider();
        Config = new VaultConfig();
        ReloadConfig();
        _notifier = notifier ?? new DiscordWebhookNotifier(() => Config);
    }

    public string VaultRoot { get; }
    public VaultConfig Config { get; private set; }
    public string UserName { get; private set; } = Environment.UserName;
    /// <summary>The GitHub account this PC is signed in as, verified with GitHub (null until verified, e.g. offline).</summary>
    public string? GitHubLogin => _identity?.Login;

    /// <summary>
    /// Admin = GitHub says this verified account is an admin or maintainer of the vault repo. Only GitHub
    /// roles count (not pdm.json, which anyone with write access could edit), so nobody can make
    /// themselves admin. For several admins, keep the vault in a (free) GitHub organization.
    /// </summary>
    public bool IsAdmin => _identity?.IsRepoAdmin == true;

    public string UserDisplayName =>
        GitHubLogin != null && Config.Team.TryGetValue(GitHubLogin, out var name) && !string.IsNullOrWhiteSpace(name) ? name : UserName;

    private string Upstream => Config.Remote + "/" + Config.Branch;

    public static async Task<VaultService> OpenAsync(string vaultRoot, INotifier? notifier = null, IGitRunner? git = null,
        IIdentityProvider? identityProvider = null, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(vaultRoot).TrimEnd('\\', '/');
        var dotGit = Path.Combine(root, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit)) throw new VaultNotFoundException(root);

        if (git == null)
        {
            var exe = GitRunner.FindGit() ?? throw new GitNotFoundException();
            git = new GitRunner(root, exe);
        }

        var service = new VaultService(root, git, notifier, identityProvider);
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
        await VerifyIdentityAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Asks GitHub which account this PC is signed in as. Retried on each vault contact until it works.</summary>
    private async Task VerifyIdentityAsync(CancellationToken ct)
    {
        if (_identity != null) return;
        try
        {
            var remote = await GetConfigValueAsync($"remote.{Config.Remote}.url", ct).ConfigureAwait(false);
            if (remote != null) _identity = await _identityProvider.GetIdentityAsync(_git, remote, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _identity = null;
        }
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
                await VerifyIdentityAsync(ct).ConfigureAwait(false);
                await GetLocksAsync(ct).ConfigureAwait(false);
                await CheckGuardIntegrityAsync(ct).ConfigureAwait(false);
            }
            catch (PdmException ex)
            {
                snapshot.IsOffline = ex is VaultOfflineException || ex is VaultAuthException;
                snapshot.ConnectionProblem = ex.Message;
            }
        }
        snapshot.LocksUnknown = !_locksKnown;
        snapshot.ViewingMilestone = await GetViewingMilestoneAsync(ct).ConfigureAwait(false);
        if (IsAdmin)
        {
            snapshot.SetupProblems = await GetSetupProblemsAsync(ct).ConfigureAwait(false);
            snapshot.Storage = _storage != null && DateTimeOffset.Now - _storage.MeasuredAt < StorageRecheck
                ? _storage
                : await MeasureStorageAsync(ct).ConfigureAwait(false);
        }
        snapshot.DownloadFolders = ParseDownloadFolders(await GetConfigValueAsync("lfs.fetchinclude", ct).ConfigureAwait(false));
        var placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.DownloadFolders != null)
        {
            var lfs = await RunAsync(new[] { "lfs", "ls-files", "--json" }, ct).ConfigureAwait(false);
            placeholders.UnionWith(LfsFile.Parse(lfs.StdOut).Where(f => !f.CheckedOut).Select(f => f.Name));
        }

        var tracked = GitParsers.SplitZ((await RunCheckedAsync("list the vault files", new[] { "ls-files", "-z" }, ct).ConfigureAwait(false)).StdOut);
        var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));
        var incoming = await GetIncomingChangesAsync(ct).ConfigureAwait(false);
        var latest = await GetLatestVersionsAsync(ct).ConfigureAwait(false);
        var incomingCheckIns = await GetIncomingCheckInCountsAsync(ct).ConfigureAwait(false);
        var frozen = await ReadFrozenListAsync(ct).ConfigureAwait(false);
        var requests = CheckInRequestList.Parse(await ReadVaultTextAsync(CheckInRequestList.RelativePath, ct).ConfigureAwait(false));
        var reviews = ReviewList.Parse(await ReadVaultTextAsync(ReviewList.RelativePath, ct).ConfigureAwait(false));
        var pins = PinnedVersions.Load(VaultRoot);
        var index = VaultIndex.ReadAll(VaultRoot);
        var usedIn = VaultIndex.WhereUsed(index);
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
            // An older version used on purpose (this PC only) differs from the latest, but isn't an edit.
            var pin = pins.Find(path);
            if (pin != null && change != null && !change.IsUntracked && !change.IsDeleted) change = null;
            var fileLock = FindLock(locks, path);
            var isLockable = lockable.Contains(path);

            FileState state;
            if (fileLock != null) state = fileLock.IsMine ? FileState.CheckedOutByMe : FileState.CheckedOutByOther;
            else if (change == null) state = FileState.Available;
            else if (change.IsUntracked) state = FileState.NotInVault;
            else if (change.IsDeleted) state = FileState.MissingLocally;
            else state = isLockable ? FileState.ModifiedWithoutCheckOut : FileState.Modified;

            var freeze = frozen.Find(path);
            if (freeze != null) state = FileState.Frozen;
            var review = freeze == null ? reviews.Find(path) : null;
            if (review != null) state = FileState.InReview;

            // A request only counts while the person it was made of still holds the check-out.
            var request = requests.Find(path);
            if (request != null && _locksKnown && (fileLock == null || !string.Equals(fileLock.Owner, request.Owner, StringComparison.OrdinalIgnoreCase)))
            {
                request = null;
            }
            if (request != null && fileLock?.IsMine == true && freeze == null && IsSettled(request)) snapshot.CheckInRequestsForMe++;

            latest.TryGetValue(path, out var version);
            incomingCheckIns.TryGetValue(path, out var newer);
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
                VaultVersion = (version?.Version ?? 0) + newer,
                PinnedVersion = pin,
                Freeze = freeze,
                FrozenBy = freeze == null ? null : Config.DisplayNameFor(freeze.By),
                Revision = frozen.RevisionOf(path),
                FreezeLockMissing = freeze != null && _locksKnown && fileLock == null,
                Review = review,
                ReviewSubmittedBy = review == null ? null : Config.DisplayNameFor(review.SubmittedBy),
                CheckInRequest = request,
                CheckInRequestedBy = request == null ? null : Config.DisplayNameFor(request.RequestedBy),
                Index = index.TryGetValue(path, out var indexEntry) ? indexEntry : null,
                UsedIn = usedIn.TryGetValue(path, out var parents) ? parents : (IReadOnlyList<string>)Array.Empty<string>(),
                NotDownloaded = placeholders.Contains(path),
            });
        }

        LinkDrawings(files);
        snapshot.Files = files;
        snapshot.RetrievedAt = DateTimeOffset.Now;
        return snapshot;
    }

    /// <summary>
    /// Connects drawings with the models they show (from the index) and flags drawings that are older
    /// than a model they show: the model was checked in after the drawing, so the drawing may need updating.
    /// </summary>
    private static void LinkDrawings(List<VaultFile> files)
    {
        var byPath = files.ToDictionary(f => f.RelativePath, f => f, StringComparer.OrdinalIgnoreCase);
        var drawingsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var staleOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var drawing in files.Where(f => f.RelativePath.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase) && f.Index != null))
        {
            var changed = new List<string>();
            foreach (var model in drawing.Index!.References)
            {
                if (!byPath.TryGetValue(model, out var modelFile)) continue;
                if (!drawingsOf.TryGetValue(model, out var list)) drawingsOf[model] = list = new List<string>();
                list.Add(drawing.RelativePath);

                if (drawing.LatestVersion != null && modelFile.LatestVersion != null && modelFile.LatestVersion.Date > drawing.LatestVersion.Date)
                {
                    changed.Add(model);
                    if (!staleOf.TryGetValue(model, out var stale)) staleOf[model] = stale = new List<string>();
                    stale.Add(drawing.RelativePath);
                }
            }
            drawing.ChangedSinceDrawing = changed;
        }

        foreach (var file in files)
        {
            if (drawingsOf.TryGetValue(file.RelativePath, out var drawings)) file.Drawings = drawings;
            if (staleOf.TryGetValue(file.RelativePath, out var stale)) file.StaleDrawings = stale;
        }
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
        // Every change goes through here, so this one check keeps a milestone view read-only.
        await EnsureNotViewingMilestoneAsync(ct).ConfigureAwait(false);
        if (fetch)
        {
            progress?.Report("Contacting the vault…");
            await FetchAsync(ct).ConfigureAwait(false);
        }
        if (!await UpstreamExistsAsync(ct).ConfigureAwait(false)) return GetLatestResult.UpToDate;

        var behind = await CountCommitsAsync("HEAD.." + Upstream, ct).ConfigureAwait(false);
        if (behind == 0) return GetLatestResult.UpToDate;

        var incoming = await GetIncomingChangesAsync(ct).ConfigureAwait(false);
        var pins = PinnedVersions.Load(VaultRoot);
        var local = (await GetLocalChangesAsync(ct).ConfigureAwait(false)).Where(c => pins.Find(c.Path) == null).ToList();

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

        // Files this PC uses an older version of: back to the latest for the update, pinned again after.
        var pinnedIncoming = pins.Pins.Where(p => incoming.Contains(p.Path)).ToList();
        foreach (var pin in pinnedIncoming) await RestoreWorkingFileAsync(pin.Path, "HEAD", ct).ConfigureAwait(false);

        progress?.Report($"Getting {incoming.Count} updated file(s) from the vault…");
        var merge = await RunAsync(new[] { "merge", "--ff", "--no-edit", "--no-stat", "-m", "Merge vault changes", Upstream }, ct, TransferTimeout).ConfigureAwait(false);
        if (!merge.Success)
        {
            if (File.Exists(Path.Combine(VaultRoot, ".git", "MERGE_HEAD")))
            {
                await RunAsync(new[] { "merge", "--abort" }, ct).ConfigureAwait(false);
            }
            foreach (var pin in pinnedIncoming) await RestoreWorkingFileAsync(pin.Path, pin.Commit, ct).ConfigureAwait(false);
            throw GitErrorClassifier.Classify(merge, "get the latest versions");
        }
        foreach (var pin in pinnedIncoming)
        {
            // A file deleted from the vault can't stay pinned.
            if (File.Exists(ToFullPath(pin.Path))) await RestoreWorkingFileAsync(pin.Path, pin.Commit, ct).ConfigureAwait(false);
            else pins.Remove(pin.Path);
        }
        pins.Save(VaultRoot);

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
        var pins = PinnedVersions.Load(VaultRoot);
        foreach (var rel in relatives)
        {
            if (pins.Find(rel) is { } pin) result.Fail(rel, PinnedError(rel, pin));
            else if (VersionCopies.IsVersionCopy(rel))
            {
                result.Fail(rel, new PdmException($"{PathList.Name(rel)} is a read-only copy of version {VersionCopies.VersionOf(rel)} of " +
                                                  $"{PathList.Name(VersionCopies.OriginalOf(rel)!)}. Check out the original to make changes."));
            }
            else if (lockable.Contains(rel)) targets.Add(rel);
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

        // Frozen files can't be checked out by anyone, admins included (they unfreeze first).
        var frozen = await ReadFrozenListAsync(ct).ConfigureAwait(false);
        foreach (var rel in targets.ToList())
        {
            var freeze = frozen.Find(rel);
            if (freeze == null) continue;
            result.Fail(rel, FrozenError(rel, freeze));
            targets.Remove(rel);
        }
        var inReview = ReviewList.Parse(await ReadVaultTextAsync(ReviewList.RelativePath, ct).ConfigureAwait(false));
        foreach (var rel in targets.ToList())
        {
            var review = inReview.Find(rel);
            if (review == null) continue;
            result.Fail(rel, InReviewError(rel, review));
            targets.Remove(rel);
        }
        if (targets.Count == 0) return result;

        // With "only download my folders", a file may be just a placeholder: get the real file before anyone edits it.
        if (await GetConfigValueAsync("lfs.fetchinclude", ct).ConfigureAwait(false) != null)
        {
            progress?.Report("Downloading\u2026");
            await DownloadCoreAsync(targets, ct).ConfigureAwait(false);
        }

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

    public async Task<OperationResult> CheckInAsync(IEnumerable<string> paths, string comment, bool keepCheckedOut = false, IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, IndexEntry?>? indexUpdates = null, IReadOnlyList<string>? previews = null)
    {
        var relatives = Normalize(paths);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CheckInCoreAsync(relatives, comment, keepCheckedOut, progress, ct, indexUpdates, previews).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OperationResult> CheckInCoreAsync(IReadOnlyList<string> relatives, string comment, bool keepCheckedOut, IProgress<string>? progress, CancellationToken ct,
        IReadOnlyDictionary<string, IndexEntry?>? indexUpdates = null, IReadOnlyList<string>? previews = null)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new PdmException("Enter a check-in comment that says what changed, for example \"added mounting holes\".");
        }
        comment = comment.Trim();
        var pinnedFiles = PinnedVersions.Load(VaultRoot);
        var pinned = relatives.Where(r => pinnedFiles.Find(r) != null).ToList();
        if (pinned.Count > 0) throw PinnedError(pinned[0], pinnedFiles.Find(pinned[0])!);
        var result = new OperationResult();

        var lockable = await GetLockableAsync(relatives, ct).ConfigureAwait(false);
        var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));

        var frozen = await ReadFrozenListAsync(ct).ConfigureAwait(false);
        foreach (var rel in relatives)
        {
            var freeze = frozen.Find(rel);
            if (freeze != null) throw FrozenError(rel, freeze);
        }
        var reviewsNow = ReviewList.Parse(await ReadVaultTextAsync(ReviewList.RelativePath, ct).ConfigureAwait(false));
        foreach (var rel in relatives)
        {
            var review = reviewsNow.Find(rel);
            if (review != null) throw InReviewError(rel, review);
        }

        // Team settings and the frozen list are admin-only, so nobody can make themselves admin or unfreeze files.
        var adminOnly = relatives.Where(IsAdminOnlyFile).ToList();
        if (adminOnly.Count > 0 && !IsAdmin)
        {
            throw new PdmException($"Only vault admins can change {PathList.Format(adminOnly)} (team settings). " +
                                   "Undo your changes to it, or ask an admin to make the change.");
        }

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

        // Checking in only reads the files, so a file that's loaded in SolidWorks (e.g. a part inside an open
        // assembly) is fine once it's saved. Only a file nobody can read stops the check-in.
        var inUse = relatives.Where(r => FileUtil.IsUnreadable(ToFullPath(r))).ToList();
        if (inUse.Count > 0) throw new FileInUseException(inUse, "Another program has them open exclusively. Close it, then check in again.");

        var changed = relatives.Where(changes.ContainsKey).ToList();

        // Commit on top of the newest vault state, so the push is a fast-forward.
        await SyncCoreAsync(progress, ct).ConfigureAwait(false);

        if (changed.Count > 0)
        {
            progress?.Report("Saving the new version…");
            // Index entries (from the SolidWorks add-in) for the files being checked in go in the same commit.
            var commitPaths = changed.Concat(WriteIndexEntries(indexUpdates, changed))
                .Concat(await CommittablePreviewsAsync(previews, ct).ConfigureAwait(false)).ToList();
            var pathspec = string.Join("\0", commitPaths) + "\0";
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
                var fileLock = FindLock(locks, rel);
                if (fileLock == null) continue; // brand-new file, never locked
                // A file that was renamed or deleted can't be unlocked by path any more.
                var unlockArgs = File.Exists(ToFullPath(rel)) ? new[] { "lfs", "unlock", rel } : new[] { "lfs", "unlock", "--id", fileLock.Id };
                var unlock = await RunAsync(unlockArgs, ct, NetworkTimeout).ConfigureAwait(false);
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
        await EnsureNotViewingMilestoneAsync(ct).ConfigureAwait(false);
        var result = new OperationResult();
        var lockable = await GetLockableAsync(relatives, ct).ConfigureAwait(false);
        var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));
        var locks = relatives.Any(lockable.Contains) ? await GetLocksAsync(ct).ConfigureAwait(false) : Array.Empty<LockInfo>();
        var frozen = await ReadFrozenListAsync(ct).ConfigureAwait(false);
        var reviewList = ReviewList.Parse(await ReadVaultTextAsync(ReviewList.RelativePath, ct).ConfigureAwait(false));

        var inUse = relatives.Where(r => changes.ContainsKey(r) && FileUtil.IsInUse(ToFullPath(r))).ToList();
        if (inUse.Count > 0) throw new FileInUseException(inUse, "Close them in SolidWorks (without saving), then try again.");

        progress?.Report("Undoing check-out…");
        string? backupFolder = null;
        var released = new List<string>();
        foreach (var rel in relatives)
        {
            var fileLock = FindLock(locks, rel);
            changes.TryGetValue(rel, out var change);

            // A frozen file's lock IS the freeze: never release it here. Stray local edits can still be thrown away.
            if (frozen.Find(rel) != null || reviewList.Find(rel) != null)
            {
                if (change == null)
                {
                    result.Fail(rel, new PdmException(frozen.Find(rel) != null
                        ? $"{PathList.Name(rel)} is frozen, not checked out, so there's nothing to undo."
                        : $"{PathList.Name(rel)} is in review, not checked out. Use Withdraw from Review to take it back."));
                    continue;
                }
                fileLock = null;
            }

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

    // ---------------------------------------------------------------- Older versions on this PC

    /// <summary>
    /// Uses an older version of a file on this PC only (e.g. an assembly with last week's part). The
    /// vault and teammates keep the latest; Get Latest keeps the choice; <see cref="ReturnToLatestAsync"/> undoes it.
    /// </summary>
    public async Task<OperationResult> UseVersionAsync(string path, FileVersion version, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var rel = ToRelativePath(path);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureNotViewingMilestoneAsync(ct).ConfigureAwait(false);
            var result = new OperationResult();
            var pins = PinnedVersions.Load(VaultRoot);
            var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));
            if (pins.Find(rel) == null && changes.ContainsKey(rel))
            {
                throw new PdmException($"{PathList.Name(rel)} has changes on this PC. Check it in or undo them before switching versions.");
            }
            if (FindLock(_lastLocks, rel)?.IsMine == true)
            {
                throw new PdmException($"{PathList.Name(rel)} is checked out to you. Check it in or undo the check-out before using an older version.");
            }

            progress?.Report($"Getting version {version.Version} of {PathList.Name(rel)}…");
            await RestoreWorkingFileAsync(rel, version.CommitId, ct).ConfigureAwait(false);
            var latest = (await GetLatestVersionsAsync(ct).ConfigureAwait(false)).TryGetValue(rel, out var head) ? head : null;
            if (latest != null && latest.CommitId == version.CommitId) pins.Remove(rel); // that's just the latest
            else pins.Set(rel, version.CommitId, version.Version);
            pins.Save(VaultRoot);
            result.Succeeded.Add(rel);
            result.Notes.Add($"This PC now uses version {version.Version} of {PathList.Name(rel)}. The vault and your teammates still have the latest.");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes (or reuses) the read-only copy of a version of a file, for assembly instances that should
    /// stay on that version. Returns the copy's full path. It's a new file until checked in; checking in
    /// the assembly that uses it brings it along.
    /// </summary>
    public async Task<string> CreateVersionCopyAsync(string path, FileVersion version, CancellationToken ct = default)
    {
        var rel = ToRelativePath(path);
        var copyRel = VersionCopies.CopyPathFor(rel, version.Version);
        var copy = ToFullPath(copyRel);
        if (File.Exists(copy)) return copy;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var scratch = Path.Combine(Path.GetTempPath(), "capstone-version-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Check the old version out into a scratch folder (LFS filters run, so it's the real file), then move it in.
            Directory.CreateDirectory(scratch);
            var gitExe = GitRunner.FindGit() ?? throw new PdmException("Git isn't installed on this PC.");
            var git = new GitRunner(scratch, gitExe);
            var restore = await git.RunAsync(new[] { "--git-dir=" + Path.Combine(VaultRoot, ".git"), "--work-tree=" + scratch,
                "restore", "--source=" + version.CommitId, "--worktree", "--", rel }, null, TransferTimeout, ct).ConfigureAwait(false);
            var restored = Path.Combine(scratch, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!restore.Success || !File.Exists(restored)) throw GitErrorClassifier.Classify(restore, $"get version {version.Version} of {PathList.Name(rel)}");

            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Move(restored, copy);
            FileUtil.SetReadOnly(copy, true);
            return copy;
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
                // Temp files; Windows cleans them up eventually.
            }
            _gate.Release();
        }
    }

    /// <summary>Goes back to the latest version of files this PC was using an older version of.</summary>
    public async Task<OperationResult> ReturnToLatestAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var relatives = Normalize(paths);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            var pins = PinnedVersions.Load(VaultRoot);
            foreach (var rel in relatives)
            {
                if (pins.Find(rel) == null)
                {
                    result.Fail(rel, new PdmException($"{PathList.Name(rel)} already uses the latest version."));
                    continue;
                }
                progress?.Report($"Getting the latest {PathList.Name(rel)}…");
                await RestoreWorkingFileAsync(rel, "HEAD", ct).ConfigureAwait(false);
                pins.Remove(rel);
                result.Succeeded.Add(rel);
            }
            pins.Save(VaultRoot);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Puts a file's content from a commit into the vault folder only (nothing staged), keeping lockable files read-only.</summary>
    private async Task RestoreWorkingFileAsync(string rel, string commit, CancellationToken ct)
    {
        var full = ToFullPath(rel);
        FileUtil.SetReadOnly(full, false);
        var restore = await RunAsync(new[] { "restore", "--source=" + commit, "--worktree", "--", rel }, ct, TransferTimeout).ConfigureAwait(false);
        if ((await GetLockableAsync(new[] { rel }, ct).ConfigureAwait(false)).Contains(rel)) FileUtil.SetReadOnly(full, true);
        if (!restore.Success)
        {
            if (FileUtil.IsInUse(full)) throw new FileInUseException(new[] { rel }, "Close it in SolidWorks (and any assembly that uses it), then try again.");
            throw GitErrorClassifier.Classify(restore, "switch the file's version");
        }
    }

    private static PdmException PinnedError(string rel, PinnedVersion pin) =>
        new PdmException($"This PC is using version {pin.Version} of {PathList.Name(rel)} (an older version). Return to the latest version before checking it out or in.");

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
            throw new PdmException("Only vault admins can break a check-out. GitHub decides who's an admin (repo Admin or Maintain role); you're signed in as " +
                                   (GitHubLogin ?? "not verified yet (are you online and signed in to GitHub?)") + ".");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            if ((await ReadFrozenListAsync(ct).ConfigureAwait(false)).Find(rel) != null)
            {
                throw new PdmException($"{PathList.Name(rel)} is frozen, not checked out. Use Unfreeze to make it editable again.");
            }
            if (ReviewList.Parse(await ReadVaultTextAsync(ReviewList.RelativePath, ct).ConfigureAwait(false)).Find(rel) != null)
            {
                throw new PdmException($"{PathList.Name(rel)} is in review, not checked out. Approve or reject the review instead.");
            }
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

    // ---------------------------------------------------------------- Freeze (admin)

    /// <summary>
    /// Freezes files so nobody can change them (e.g. released for production). The admin takes and
    /// keeps each file's lock, which is what stops check-outs and pushes; .pdm/frozen.json records
    /// who froze it, when and why, so everyone's vault view shows it.
    /// </summary>
    public async Task<OperationResult> FreezeAsync(IEnumerable<string> paths, string reason, IProgress<string>? progress = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? revisions = null)
    {
        RequireAdmin("freeze files");
        if (string.IsNullOrWhiteSpace(reason)) throw new PdmException("Enter a reason, for example \"Released for production, rev A\".");
        reason = reason.Trim();
        var relatives = Normalize(paths);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FreezeCoreAsync(relatives, reason, progress, ct, revisions).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OperationResult> FreezeCoreAsync(IReadOnlyList<string> relatives, string reason, IProgress<string>? progress, CancellationToken ct,
        IReadOnlyDictionary<string, string>? revisions)
    {
        var result = new OperationResult();
        var lockable = await GetLockableAsync(relatives, ct).ConfigureAwait(false);
        await SyncCoreAsync(progress, ct).ConfigureAwait(false);
        var frozen = await ReadFrozenListAsync(ct).ConfigureAwait(false);
        var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));
        var locks = await GetLocksAsync(ct).ConfigureAwait(false);

        progress?.Report("Freezing…");
        var toFreeze = new List<string>();
        foreach (var rel in relatives)
        {
            var name = PathList.Name(rel);
            if (!lockable.Contains(rel))
            {
                result.Fail(rel, new PdmException($"{name} isn't a lockable (CAD) file, so it can't be frozen."));
                continue;
            }
            if (frozen.Find(rel) != null)
            {
                result.Fail(rel, new PdmException($"{name} is already frozen."));
                continue;
            }
            if (changes.ContainsKey(rel))
            {
                result.Fail(rel, new PdmException($"{name} has changes on this PC that aren't in the vault. Check it in (or undo) before freezing it."));
                continue;
            }

            var fileLock = FindLock(locks, rel);
            if (fileLock != null && !fileLock.IsMine)
            {
                result.Fail(rel, new PdmException($"{name} is checked out by {Config.DisplayNameFor(fileLock.Owner)}. Ask them to check it in before freezing it."));
                continue;
            }
            if (fileLock == null)
            {
                var lockResult = await RunAsync(new[] { "lfs", "lock", "--json", rel }, ct, NetworkTimeout).ConfigureAwait(false);
                if (!lockResult.Success)
                {
                    result.Fail(rel, GitErrorClassifier.Classify(lockResult, "freeze " + name));
                    continue;
                }
            }
            toFreeze.Add(rel);
        }
        if (toFreeze.Count == 0) return result;

        var by = GitHubLogin ?? UserName;
        var given = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in toFreeze)
        {
            // Each freeze is a release: the next revision letter, unless one is given (e.g. a drawing's PDF matches the drawing).
            var revision = revisions != null && revisions.TryGetValue(rel, out var forced) && forced.Length > 0
                ? forced
                : Revisions.Next(frozen.RevisionOf(rel));
            given[rel] = revision;
            frozen.Revisions[rel] = revision;
            frozen.Files.Add(new FrozenEntry { Path = rel, By = by, At = DateTimeOffset.Now, Reason = reason, Revision = revision });
            FileUtil.SetReadOnly(ToFullPath(rel), true);
        }
        var summary = string.Join(", ", toFreeze.Select(r => $"{PathList.Name(r)} Rev {given[r]}"));
        await SaveFrozenListAsync(frozen, $"Froze {summary}: {reason}", progress, ct).ConfigureAwait(false);
        result.Succeeded.AddRange(toFreeze);
        result.Notes.Add($"Released as: {summary}.");

        await NotifyAsync(new VaultEvent(VaultEventKind.Frozen, UserDisplayName, toFreeze) { Comment = reason, Revisions = given }, result).ConfigureAwait(false);
        return result;
    }

    /// <summary>Removes files from the frozen list and releases the freeze lock (forcing it if another admin froze it).</summary>
    public async Task<OperationResult> UnfreezeAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        RequireAdmin("unfreeze files");
        var relatives = Normalize(paths);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            await SyncCoreAsync(progress, ct).ConfigureAwait(false);
            var frozen = await ReadFrozenListAsync(ct).ConfigureAwait(false);
            var locks = await GetLocksAsync(ct).ConfigureAwait(false);

            progress?.Report("Unfreezing…");
            var unfrozen = new List<string>();
            foreach (var rel in relatives)
            {
                var entry = frozen.Find(rel);
                if (entry == null)
                {
                    result.Fail(rel, new PdmException($"{PathList.Name(rel)} isn't frozen."));
                    continue;
                }
                frozen.Files.Remove(entry);
                unfrozen.Add(rel);
            }
            if (unfrozen.Count == 0) return result;

            await SaveFrozenListAsync(frozen, $"Unfroze {PathList.Format(unfrozen)}", progress, ct).ConfigureAwait(false);

            foreach (var rel in unfrozen)
            {
                var fileLock = FindLock(locks, rel);
                if (fileLock == null) continue;
                var args = fileLock.IsMine
                    ? new[] { "lfs", "unlock", rel }
                    : new[] { "lfs", "unlock", "--force", "--id", fileLock.Id };
                var unlock = await RunAsync(args, ct, NetworkTimeout).ConfigureAwait(false);
                if (!unlock.Success)
                {
                    result.Warnings.Add($"{PathList.Name(rel)} was unfrozen, but its lock couldn't be released ({GitErrorClassifier.Classify(unlock, "release the lock").Message}). Use Break Lock.");
                }
                FileUtil.SetReadOnly(ToFullPath(rel), true);
            }
            result.Succeeded.AddRange(unfrozen);

            await NotifyAsync(new VaultEvent(VaultEventKind.Unfrozen, UserDisplayName, unfrozen), result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The revision letters these files would get if frozen now (e.g. to name a drawing's PDF before freezing).</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetNextRevisionsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var relatives = Normalize(paths);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FetchAsync(ct).ConfigureAwait(false);
            var frozen = await ReadFrozenListAsync(ct).ConfigureAwait(false);
            return relatives.ToDictionary(r => r, r => Revisions.Next(frozen.RevisionOf(r)), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FrozenList> ReadFrozenListAsync(CancellationToken ct) =>
        FrozenList.Parse(await ReadVaultTextAsync(FrozenList.RelativePath, ct).ConfigureAwait(false));

    private async Task SaveFrozenListAsync(FrozenList frozen, string message, IProgress<string>? progress, CancellationToken ct)
    {
        WriteVaultText(FrozenList.RelativePath, frozen.Serialize());
        await CommitAndPushAsync(new[] { FrozenList.RelativePath }, message, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Reads a vault file as the vault (GitHub) has it, falling back to this PC's copy.</summary>
    private async Task<string?> ReadVaultTextAsync(string relativePath, CancellationToken ct)
    {
        if (await UpstreamExistsAsync(ct).ConfigureAwait(false))
        {
            var show = await RunAsync(new[] { "show", Upstream + ":" + relativePath }, ct).ConfigureAwait(false);
            return show.Success ? show.StdOut : null;
        }
        var local = ToFullPath(relativePath);
        return File.Exists(local) ? File.ReadAllText(local) : null;
    }

    private void WriteVaultText(string relativePath, string text)
    {
        var full = ToFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>Commits exactly these paths (adds, edits and deletions) and pushes, retrying if the vault moved.</summary>
    private async Task CommitAndPushAsync(IReadOnlyList<string> relativePaths, string message, IProgress<string>? progress, CancellationToken ct)
    {
        var pathspec = string.Join("\0", relativePaths) + "\0";
        await RunCheckedAsync("save to the vault", new[] { "add", "--all", "--pathspec-from-file=-", "--pathspec-file-nul" }, ct, TransferTimeout, pathspec).ConfigureAwait(false);
        var commit = await RunAsync(new[] { "commit", "--quiet", "-m", message, "--pathspec-from-file=-", "--pathspec-file-nul" }, ct, null, pathspec).ConfigureAwait(false);
        var nothingToCommit = (commit.StdOut + commit.StdErr).IndexOf("nothing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              (commit.StdOut + commit.StdErr).IndexOf("no changes added", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!commit.Success && !nothingToCommit) throw GitErrorClassifier.Classify(commit, "save to the vault");
        try
        {
            progress?.Report("Sending to the vault…");
            await PushWithRetryAsync(progress, ct).ConfigureAwait(false);
        }
        catch (PdmException ex)
        {
            throw PushRejectedException.NotSent(ex);
        }
    }

    // ---------------------------------------------------------------- Review before release

    /// <summary>
    /// Submits files for review before release. The submitter takes (or keeps) each file's lock, so
    /// nobody can change it while it's reviewed; an admin then approves (releases) or rejects it.
    /// </summary>
    public async Task<OperationResult> SubmitForReviewAsync(IEnumerable<string> paths, string note, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var relatives = Normalize(paths);
        note = (note ?? "").Trim();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            await SyncCoreAsync(progress, ct).ConfigureAwait(false);
            await VerifyIdentityAsync(ct).ConfigureAwait(false);
            if (GitHubLogin == null) throw new PdmException("Jarvis couldn't confirm your GitHub account (are you online?), so it can't submit for review yet.");

            var lockable = await GetLockableAsync(relatives, ct).ConfigureAwait(false);
            var frozen = await ReadFrozenListAsync(ct).ConfigureAwait(false);
            var reviews = ReviewList.Parse(await ReadVaultTextAsync(ReviewList.RelativePath, ct).ConfigureAwait(false));
            var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));
            var locks = await GetLocksAsync(ct).ConfigureAwait(false);

            var submitted = new List<string>();
            foreach (var rel in relatives)
            {
                var name = PathList.Name(rel);
                PdmException? problem =
                    !lockable.Contains(rel) ? new PdmException($"{name} isn't a lockable (CAD) file, so it can't go through review.")
                    : frozen.Find(rel) != null ? new PdmException($"{name} is already released (frozen).")
                    : reviews.Find(rel) != null ? new PdmException($"{name} is already in review.")
                    : changes.ContainsKey(rel) ? new PdmException($"{name} has changes on this PC that aren't checked in. Check it in first, so the reviewer sees your latest version.")
                    : FindLock(locks, rel) is LockInfo l && !l.IsMine ? new LockHeldException(rel, Config.DisplayNameFor(l.Owner), l.LockedAt)
                    : null;
                if (problem != null)
                {
                    result.Fail(rel, problem);
                    continue;
                }
                if (FindLock(locks, rel) == null)
                {
                    var take = await RunAsync(new[] { "lfs", "lock", "--json", rel }, ct, NetworkTimeout).ConfigureAwait(false);
                    if (!take.Success)
                    {
                        result.Fail(rel, GitErrorClassifier.Classify(take, "submit " + name + " for review"));
                        continue;
                    }
                }
                FileUtil.SetReadOnly(ToFullPath(rel), true);
                reviews.Reviews.Add(new ReviewEntry { Path = rel, SubmittedBy = GitHubLogin, SubmittedAt = DateTimeOffset.Now, Note = note });
                submitted.Add(rel);
            }
            if (submitted.Count == 0) return result;

            await SaveReviewsAsync(reviews, $"Submitted {PathList.Format(submitted)} for review", progress, ct).ConfigureAwait(false);
            result.Succeeded.AddRange(submitted);
            await NotifyAsync(new VaultEvent(VaultEventKind.ReviewSubmitted, UserDisplayName, submitted) { Comment = note }, result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Takes your own files back out of review (admins can withdraw anyone's).</summary>
    public async Task<OperationResult> WithdrawReviewAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default) =>
        await EndReviewAsync(Normalize(paths), approve: false, reason: "", withdraw: true, progress, ct).ConfigureAwait(false);

    /// <summary>Admin: approves files in review and releases them (freeze with the next revision letter).</summary>
    public async Task<OperationResult> ApproveReviewAsync(IEnumerable<string> paths, string reason, IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? revisions = null)
    {
        RequireAdmin("approve reviews");
        if (string.IsNullOrWhiteSpace(reason)) throw new PdmException("Enter a release note, for example \"Approved for production\".");
        return await EndReviewAsync(Normalize(paths), approve: true, reason.Trim(), withdraw: false, progress, ct, revisions).ConfigureAwait(false);
    }

    /// <summary>Admin: rejects files in review with a reason; they can be edited again.</summary>
    public async Task<OperationResult> RejectReviewAsync(IEnumerable<string> paths, string reason, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        RequireAdmin("reject reviews");
        if (string.IsNullOrWhiteSpace(reason)) throw new PdmException("Enter a reason, so the submitter knows what to change.");
        return await EndReviewAsync(Normalize(paths), approve: false, reason.Trim(), withdraw: false, progress, ct).ConfigureAwait(false);
    }

    private async Task<OperationResult> EndReviewAsync(IReadOnlyList<string> relatives, bool approve, string reason, bool withdraw,
        IProgress<string>? progress, CancellationToken ct, IReadOnlyDictionary<string, string>? revisions = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            await SyncCoreAsync(progress, ct).ConfigureAwait(false);
            await VerifyIdentityAsync(ct).ConfigureAwait(false);
            var reviews = ReviewList.Parse(await ReadVaultTextAsync(ReviewList.RelativePath, ct).ConfigureAwait(false));
            var locks = await GetLocksAsync(ct).ConfigureAwait(false);

            var ended = new List<ReviewEntry>();
            foreach (var rel in relatives)
            {
                var entry = reviews.Find(rel);
                if (entry == null)
                {
                    result.Fail(rel, new PdmException($"{PathList.Name(rel)} isn't in review."));
                    continue;
                }
                if (withdraw && !IsAdmin && !string.Equals(entry.SubmittedBy, GitHubLogin, StringComparison.OrdinalIgnoreCase))
                {
                    result.Fail(rel, new PdmException($"{PathList.Name(rel)} was submitted by {Config.DisplayNameFor(entry.SubmittedBy)}; only they or an admin can withdraw it."));
                    continue;
                }
                reviews.Reviews.Remove(entry);
                ended.Add(entry);
            }
            if (ended.Count == 0) return result;

            var paths = ended.Select(e => e.Path).ToList();
            var what = approve ? "Approved" : withdraw ? "Withdrew from review" : "Rejected";
            await SaveReviewsAsync(reviews, $"{what} {PathList.Format(paths)}" + (reason.Length > 0 ? $": {reason}" : ""), progress, ct).ConfigureAwait(false);

            // Release the submitter's hold on each file.
            foreach (var path in paths)
            {
                var fileLock = FindLock(locks, path);
                if (fileLock == null) continue;
                var args = fileLock.IsMine ? new[] { "lfs", "unlock", path } : new[] { "lfs", "unlock", "--force", "--id", fileLock.Id };
                var unlock = await RunAsync(args, ct, NetworkTimeout).ConfigureAwait(false);
                if (!unlock.Success) result.Warnings.Add($"{PathList.Name(path)}: {GitErrorClassifier.Classify(unlock, "release the review hold").Message}");
            }

            if (approve)
            {
                var freeze = await FreezeCoreAsync(paths, reason, progress, ct, revisions).ConfigureAwait(false);
                freeze.Warnings.InsertRange(0, result.Warnings);
                freeze.Failures.InsertRange(0, result.Failures);
                return freeze;
            }

            result.Succeeded.AddRange(paths);
            var submitters = string.Join(", ", ended.Select(e => Config.DisplayNameFor(e.SubmittedBy)).Distinct());
            await NotifyAsync(withdraw
                ? new VaultEvent(VaultEventKind.ReviewWithdrawn, UserDisplayName, paths)
                : new VaultEvent(VaultEventKind.ReviewRejected, UserDisplayName, paths) { Comment = reason, PreviousOwner = submitters }, result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveReviewsAsync(ReviewList reviews, string message, IProgress<string>? progress, CancellationToken ct)
    {
        WriteVaultText(ReviewList.RelativePath, reviews.Serialize());
        await CommitAndPushAsync(new[] { ReviewList.RelativePath }, message, progress, ct).ConfigureAwait(false);
    }

    private PdmException InReviewError(string rel, ReviewEntry review) =>
        new PdmException($"{PathList.Name(rel)} is in review (submitted by {Config.DisplayNameFor(review.SubmittedBy)}), so it can't be changed right now. " +
                         "An admin approves or rejects it, or the submitter can withdraw it.");

    // ---------------------------------------------------------------- Admin check-in requests

    /// <summary>
    /// Admin: asks whoever has the file checked out to check in their last saved version. Their own
    /// Jarvis does it (the file only exists on their PC) the next time it contacts the vault.
    /// </summary>
    public async Task<OperationResult> RequestCheckInAsync(string path, string note, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        RequireAdmin("request a check-in");
        var rel = ToRelativePath(path);
        note = (note ?? "").Trim();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            await SyncCoreAsync(progress, ct).ConfigureAwait(false);
            if ((await ReadFrozenListAsync(ct).ConfigureAwait(false)).Find(rel) != null)
            {
                throw new PdmException($"{PathList.Name(rel)} is frozen, not checked out.");
            }
            var locks = await GetLocksAsync(ct).ConfigureAwait(false);
            var fileLock = FindLock(locks, rel) ?? throw new PdmException($"{PathList.Name(rel)} isn't checked out by anyone, so there's nothing to request.");
            if (fileLock.IsMine) throw new PdmException($"{PathList.Name(rel)} is checked out to you. Just check it in.");

            var requests = CheckInRequestList.Parse(await ReadVaultTextAsync(CheckInRequestList.RelativePath, ct).ConfigureAwait(false));
            // Drop requests that are done or moot (the person no longer holds that check-out), and any older one for this file.
            requests.Requests.RemoveAll(r =>
                SamePath(r.Path, rel) ||
                !string.Equals(FindLock(locks, r.Path)?.Owner, r.Owner, StringComparison.OrdinalIgnoreCase));
            requests.Requests.Add(new AdminCheckInRequest
            {
                Path = rel,
                Owner = fileLock.Owner,
                RequestedBy = GitHubLogin ?? UserName,
                RequestedAt = DateTimeOffset.Now,
                Note = note,
            });

            var owner = Config.DisplayNameFor(fileLock.Owner);
            WriteVaultText(CheckInRequestList.RelativePath, requests.Serialize());
            await CommitAndPushAsync(new[] { CheckInRequestList.RelativePath }, $"Requested check-in of {PathList.Name(rel)} from {owner}", progress, ct).ConfigureAwait(false);

            result.Succeeded.Add(rel);
            result.Notes.Add($"{owner}'s PC will check in their last saved version of {PathList.Name(rel)} and release it, the next time Jarvis " +
                             "or SolidWorks is running there (within about five minutes if it's on). Unsaved changes in their SolidWorks aren't included.\n\n" +
                             "If their PC is off and you can't wait, use Break Lock instead (their saved work then stays on their PC only).");
            await NotifyAsync(new VaultEvent(VaultEventKind.CheckInRequested, UserDisplayName, new[] { rel }) { PreviousOwner = owner, Comment = note }, result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Carries out admin requests for files checked out to this user: checks in the saved file as it
    /// is on disk (even if SolidWorks has it open, when Windows allows reading it) and releases the lock.
    /// </summary>
    public async Task<OperationResult> ProcessCheckInRequestsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            await FetchAsync(ct).ConfigureAwait(false);
            var requests = CheckInRequestList.Parse(await ReadVaultTextAsync(CheckInRequestList.RelativePath, ct).ConfigureAwait(false));
            if (requests.Requests.Count == 0) return result;

            var locks = await GetLocksAsync(ct).ConfigureAwait(false);
            var frozen = await ReadFrozenListAsync(ct).ConfigureAwait(false);
            var mine = requests.Requests
                .Where(r => frozen.Find(r.Path) == null)
                .Where(r => FindLock(locks, r.Path) is LockInfo l && l.IsMine && string.Equals(l.Owner, r.Owner, StringComparison.OrdinalIgnoreCase))
                .Where(IsSettled)
                .ToList();
            if (mine.Count == 0) return result;

            progress?.Report("Checking in files an admin asked for…");
            await SyncCoreAsync(progress, ct, fetch: false).ConfigureAwait(false);

            var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));
            var changed = mine.Select(r => r.Path).Where(changes.ContainsKey).ToList();
            var all = mine.Select(r => r.Path).ToList();
            var requesters = string.Join(", ", mine.Select(r => Config.DisplayNameFor(r.RequestedBy)).Distinct());

            foreach (var request in mine) requests.Requests.Remove(request);
            WriteVaultText(CheckInRequestList.RelativePath, requests.Serialize());

            var comment = changed.Count > 0
                ? $"Checked in at {requesters}'s request (last saved version)"
                : $"Released at {requesters}'s request (no saved changes)";
            try
            {
                await CommitAndPushAsync(changed.Concat(new[] { CheckInRequestList.RelativePath }).ToList(), comment, progress, ct).ConfigureAwait(false);
            }
            catch (PdmException)
            {
                // If nothing was committed (e.g. Windows wouldn't let Git read a file), put the request back for next time.
                if (await IsUncommittedAsync(CheckInRequestList.RelativePath, ct).ConfigureAwait(false))
                {
                    await RunAsync(new[] { "reset", "--quiet", "--", CheckInRequestList.RelativePath }, ct).ConfigureAwait(false);
                    await RunAsync(new[] { "checkout", "HEAD", "--", CheckInRequestList.RelativePath }, ct).ConfigureAwait(false);
                }
                throw;
            }

            foreach (var rel in all)
            {
                var unlock = await RunAsync(new[] { "lfs", "unlock", rel }, ct, NetworkTimeout).ConfigureAwait(false);
                if (unlock.Success) FileUtil.SetReadOnly(ToFullPath(rel), true);
                else result.Warnings.Add($"{PathList.Name(rel)} was checked in, but its check-out couldn't be released: {GitErrorClassifier.Classify(unlock, "release the check-out").Message}");
            }
            result.Succeeded.AddRange(all);

            result.Notes.Add(changed.Count > 0
                ? $"{requesters} needed {PathList.Format(all)}, so your last saved version was checked in and your check-out was released.\n\n" +
                  "Changes you hadn't saved in SolidWorks were NOT included. If you had any, use Save As to keep a copy outside the vault, then talk to whoever needs the file."
                : $"{requesters} needed {PathList.Format(all)}. You had no saved changes, so your check-out was simply released.");

            if (changed.Count > 0)
            {
                var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var rel in changed) versions[rel] = await CountCommitsAsync("HEAD", ct, "--no-merges", rel).ConfigureAwait(false);
                await NotifyAsync(new VaultEvent(VaultEventKind.CheckedIn, UserDisplayName, changed) { Comment = comment, Versions = versions }, result).ConfigureAwait(false);
            }
            var releasedOnly = all.Except(changed, StringComparer.OrdinalIgnoreCase).ToList();
            if (releasedOnly.Count > 0)
            {
                await NotifyAsync(new VaultEvent(VaultEventKind.UndoCheckOut, UserDisplayName, releasedOnly), result).ConfigureAwait(false);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // A request is only acted on after this PC has seen it in the vault for a few minutes: long enough
    // for the vault guard to undo a forged one (added by a non-admin with Git commands). Uses this PC's
    // clock from when it first saw the request, not the date written in it, which could be faked.
    private static readonly TimeSpan RequestSettleTime = TimeSpan.FromMinutes(3);
    private readonly Dictionary<string, DateTimeOffset> _requestFirstSeen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    private bool IsSettled(AdminCheckInRequest request)
    {
        var key = $"{request.Path}|{request.Owner}|{request.RequestedBy}|{request.RequestedAt:o}";
        var now = DateTimeOffset.Now;
        if (!_requestFirstSeen.TryGetValue(key, out var firstSeen))
        {
            _requestFirstSeen[key] = now;
            return false;
        }
        return now - firstSeen >= RequestSettleTime;
    }

    private async Task<bool> IsUncommittedAsync(string relativePath, CancellationToken ct) =>
        !(await RunAsync(new[] { "diff", "--quiet", "HEAD", "--", relativePath }, ct).ConfigureAwait(false)).Success;

    private PdmException FrozenError(string rel, FrozenEntry freeze) =>
        new FrozenFileException(rel, Config.DisplayNameFor(freeze.By), freeze.At, freeze.Reason, IsAdmin);

    /// <summary>
    /// 3D preview files to include in a commit: ones that exist (new or updated) and ones that were
    /// tracked but are now gone (renamed/deleted). Anything outside .pdm/preview/ is ignored.
    /// </summary>
    private async Task<List<string>> CommittablePreviewsAsync(IReadOnlyList<string>? previews, CancellationToken ct)
    {
        var result = new List<string>();
        if (previews == null) return result;
        foreach (var rel in previews.Select(ToRelativePath).Where(VaultPaths.IsPreview).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Windows ignores case but Git doesn't (SolidWorks saves "x.STL" when asked for "x.stl"): use the name as it is on disk.
            var onDisk = FindWithActualCase(ToFullPath(rel));
            if (onDisk != null)
            {
                result.Add(ToRelativePath(onDisk));
                continue;
            }
            var tracked = await RunAsync(new[] { "ls-files", "--", rel }, ct).ConfigureAwait(false);
            if (tracked.Success && tracked.StdOut.Trim().Length > 0) result.Add(rel);
        }
        return result;
    }

    /// <summary>The file's full path with the letter case it really has on disk, or null if it doesn't exist.</summary>
    private static string? FindWithActualCase(string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath);
        if (folder == null || !Directory.Exists(folder)) return null;
        var name = Path.GetFileName(fullPath);
        var match = Directory.EnumerateFiles(folder).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        return match == null ? null : Path.Combine(folder, Path.GetFileName(match));
    }

    /// <summary>Team settings and admin-managed lists: only admins may check these in.</summary>
    private static bool IsAdminOnlyFile(string rel) =>
        SamePath(rel, VaultConfig.FileName) || SamePath(rel, FrozenList.RelativePath) || SamePath(rel, CheckInRequestList.RelativePath) ||
        rel.Replace('\\', '/').StartsWith(".github/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes (or deletes, for null) index entries for the given files; returns the entry paths to commit.</summary>
    private List<string> WriteIndexEntries(IReadOnlyDictionary<string, IndexEntry?>? updates, IReadOnlyCollection<string>? onlyFor = null)
    {
        var paths = new List<string>();
        if (updates == null) return paths;
        foreach (var pair in updates)
        {
            var rel = ToRelativePath(pair.Key);
            if (onlyFor != null && !onlyFor.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
            var entryPath = VaultIndex.PathFor(rel);
            var full = ToFullPath(entryPath);
            if (pair.Value == null)
            {
                if (!File.Exists(full)) continue;
                File.Delete(full);
            }
            else
            {
                pair.Value.UpdatedAt = DateTimeOffset.Now;
                pair.Value.UpdatedBy = UserDisplayName;
                WriteVaultText(entryPath, VaultIndex.Serialize(pair.Value));
            }
            paths.Add(entryPath);
        }
        return paths;
    }

    /// <summary>
    /// Saves index entries (references, properties, saved-in version) without checking in any CAD
    /// file. Used by the SolidWorks add-in's Rebuild Index. Null entries remove a file's entry.
    /// </summary>
    public async Task<OperationResult> UpdateIndexAsync(IReadOnlyDictionary<string, IndexEntry?> entries, IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyList<string>? previews = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            await SyncCoreAsync(progress, ct).ConfigureAwait(false);
            var paths = WriteIndexEntries(entries).Concat(await CommittablePreviewsAsync(previews, ct).ConfigureAwait(false)).ToList();
            if (paths.Count == 0) return result;
            await CommitAndPushAsync(paths, $"Updated the vault index ({entries.Count} file(s))", progress, ct).ConfigureAwait(false);
            result.Succeeded.AddRange(entries.Keys.Select(ToRelativePath));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- Vault setup (guard, storage rules)

    /// <summary>What's missing or tampered with in the vault's Jarvis setup, as the vault (GitHub) has it.</summary>
    private async Task<List<string>> GetSetupProblemsAsync(CancellationToken ct)
    {
        var problems = new List<string>();
        var workflow = await ReadVaultTextAsync(VaultSetup.GuardWorkflowPath, ct).ConfigureAwait(false);
        var script = await ReadVaultTextAsync(VaultSetup.GuardScriptPath, ct).ConfigureAwait(false);
        if (workflow == null || script == null)
        {
            problems.Add("The vault guard isn't installed on GitHub, so pushes made with Git commands aren't checked.");
        }
        else if (!VaultSetup.SameText(workflow, VaultSetup.GuardWorkflow) || !VaultSetup.SameText(script, VaultSetup.GuardScript))
        {
            var last = await RunAsync(new[] { "log", "-1", "--format=%an on %ad", "--date=format:%b %d %H:%M", Upstream, "--", ".github" }, ct).ConfigureAwait(false);
            problems.Add("The vault guard was changed" + (last.Success && last.StdOut.Trim().Length > 0 ? $" (last change by {last.StdOut.Trim()})" : "") +
                         ", or this app has a newer version of it.");
        }

        var attributes = await ReadVaultTextAsync(VaultSetup.AttributesPath, ct).ConfigureAwait(false) ?? "";
        if (attributes.IndexOf(VaultSetup.PreviewAttributesRule, StringComparison.Ordinal) < 0)
        {
            problems.Add("The vault isn't set up to store 3D previews efficiently.");
        }
        return problems;
    }

    /// <summary>
    /// The guard's trigger file is the one thing a push can switch off. Any PC that notices it was removed
    /// or altered (after having been installed) posts a Discord alert, once per change.
    /// </summary>
    private async Task CheckGuardIntegrityAsync(CancellationToken ct)
    {
        var everInstalled = await RunAsync(new[] { "log", "-1", "--format=%H", Upstream, "--", VaultSetup.GuardWorkflowPath }, ct).ConfigureAwait(false);
        if (!everInstalled.Success || everInstalled.StdOut.Trim().Length == 0) return;

        var workflow = await ReadVaultTextAsync(VaultSetup.GuardWorkflowPath, ct).ConfigureAwait(false);
        if (workflow != null && VaultSetup.SameText(workflow, VaultSetup.GuardWorkflow)) return;

        var last = await RunAsync(new[] { "log", "-1", "--format=%H%x1f%an", Upstream, "--", ".github" }, ct).ConfigureAwait(false);
        var parts = last.StdOut.Trim().Split('\x1f');
        if (parts[0].Length == 0 || parts[0] == await GetConfigValueAsync("pdm.guardAlerted", ct).ConfigureAwait(false)) return;

        var e = new VaultEvent(VaultEventKind.GuardTampered, UserDisplayName, Array.Empty<string>())
        {
            Comment = workflow == null ? "removed" : "changed",
            PreviousOwner = parts.Length > 1 ? parts[1] : null,
        };
        await NotifyAsync(e, new OperationResult()).ConfigureAwait(false);
        await RunAsync(new[] { "config", "--local", "pdm.guardAlerted", parts[0] }, ct).ConfigureAwait(false);
    }

    /// <summary>Admin: installs or repairs the vault guard and storage rules, then pushes them.</summary>
    public async Task<OperationResult> UpdateVaultSetupAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        RequireAdmin("update the vault setup");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new OperationResult();
            await SyncCoreAsync(progress, ct).ConfigureAwait(false);

            WriteVaultText(VaultSetup.GuardWorkflowPath, VaultSetup.GuardWorkflow.Replace("\r\n", "\n"));
            WriteVaultText(VaultSetup.GuardScriptPath, VaultSetup.GuardScript.Replace("\r\n", "\n"));
            var attributesFile = ToFullPath(VaultSetup.AttributesPath);
            var attributes = File.Exists(attributesFile) ? File.ReadAllText(attributesFile) : "";
            if (attributes.IndexOf(VaultSetup.PreviewAttributesRule, StringComparison.Ordinal) < 0)
            {
                var separator = attributes.Length == 0 || attributes.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n";
                File.WriteAllText(attributesFile, attributes + separator + "\n# 3D previews made by the SolidWorks add-in\n" + VaultSetup.PreviewAttributesRule + "\n");
            }

            await CommitAndPushAsync(new[] { VaultSetup.GuardWorkflowPath, VaultSetup.GuardScriptPath, VaultSetup.AttributesPath },
                "Updated vault setup (vault guard, 3D preview storage)", progress, ct).ConfigureAwait(false);
            result.Succeeded.Add(VaultSetup.GuardWorkflowPath);
            result.Notes.Add("The vault guard is installed. From now on GitHub checks every push and undoes ones that break the vault rules " +
                             "(you'll see runs under the repo's Actions tab).");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- Storage meter

    private static readonly TimeSpan StorageRecheck = TimeSpan.FromMinutes(30);
    private StorageUsage? _storage;

    /// <summary>
    /// Measures Git LFS storage: every distinct version of every CAD file in the vault's history,
    /// which is what GitHub counts against the free storage (it never deletes old versions).
    /// </summary>
    public async Task<StorageUsage> GetStorageUsageAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await MeasureStorageAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StorageUsage> MeasureStorageAsync(CancellationToken ct)
    {
        // --all walks every commit; the upstream ref makes sure versions this PC hasn't merged yet count too.
        var all = await RunAsync(new[] { "lfs", "ls-files", "--all", "--json" }, ct, TransferTimeout).ConfigureAwait(false);
        var latest = await RunAsync(new[] { "lfs", "ls-files", "--json" }, ct).ConfigureAwait(false);
        var versions = LfsFile.Parse(all.StdOut).GroupBy(f => f.Oid).Select(g => g.First()).ToList();
        var current = LfsFile.Parse(latest.StdOut);
        _storage = new StorageUsage
        {
            AllVersionsBytes = versions.Sum(f => f.Size),
            VersionCount = versions.Count,
            LatestBytes = current.Sum(f => f.Size),
            FileCount = current.Count,
            LimitGB = Config.StorageLimitGB,
            WarningPercent = Config.StorageWarningPercent,
            MeasuredAt = DateTimeOffset.Now,
        };
        return _storage;
    }

    // ---------------------------------------------------------------- Export warning

    /// <summary>
    /// If any of these files is a new or changed export (STEP, PDF, DXF, ...), returns a message asking
    /// whether to check it in anyway; null when there's nothing to warn about (or pdm.json turns it off).
    /// </summary>
    public async Task<string?> GetExportWarningAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        if (!Config.WarnOnExports) return null;
        var relatives = paths.Select(ToRelativePath).Where(ExportFiles.IsExport).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (relatives.Count == 0) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var changes = ToMap(await GetLocalChangesAsync(ct).ConfigureAwait(false));
            var exports = relatives.Where(r => changes.TryGetValue(r, out var c) && !c.IsDeleted).ToList();
            if (exports.Count == 0) return null;

            long SizeOf(string rel)
            {
                var info = new FileInfo(ToFullPath(rel));
                return info.Exists ? info.Length : 0;
            }
            var list = string.Join("\n", exports.Select(r => $"• {r} ({StorageUsage.FormatBytes(SizeOf(r))})"));
            return $"{(exports.Count == 1 ? "This file looks" : "These files look")} like an export (STEP, PDF, DXF, ...):\n{list}\n\n" +
                   "Exports can be made again from the model whenever they're needed, and every check-in keeps a full copy forever " +
                   $"in the vault's free {Config.StorageLimitGB:0.#} GB.\n\n" +
                   "Check in anyway? (Do check in files you actually send out, like a DXF for cutting or a PDF for a review.)";
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- Only download my folders

    /// <summary>
    /// Folders this PC downloads CAD files for (Git LFS lfs.fetchinclude); null means everything.
    /// Files elsewhere stay as small placeholders until downloaded, which saves the team's monthly
    /// download allowance. 3D previews (.pdm) are always downloaded.
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetDownloadFoldersAsync(CancellationToken ct = default)
    {
        var value = await GetConfigValueAsync("lfs.fetchinclude", ct).ConfigureAwait(false);
        return ParseDownloadFolders(value);
    }

    /// <summary>Sets the folders to download (null or empty = everything) and downloads what's newly included.</summary>
    public async Task SetDownloadFoldersAsync(IReadOnlyList<string>? folders, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cleaned = folders?.Select(f => f.Replace('\\', '/').Trim('/')).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (cleaned == null || cleaned.Count == 0)
            {
                await RunAsync(new[] { "config", "--local", "--unset", "lfs.fetchinclude" }, ct).ConfigureAwait(false);
            }
            else
            {
                // Files in the vault root and the .pdm folder (3D previews, index) always come down.
                var patterns = cleaned.Select(f => f + "/**").Concat(new[] { "/*.*", ".pdm/**" });
                await RunCheckedAsync("save the download folders", new[] { "config", "--local", "lfs.fetchinclude", string.Join(",", patterns) }, ct).ConfigureAwait(false);
            }

            progress?.Report("Downloading files in the chosen folders…");
            await RunCheckedAsync("download files", new[] { "lfs", "pull" }, ct, TransferTimeout).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Downloads specific files (or everything under specific folders) even if their folder isn't chosen.</summary>
    public async Task DownloadAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var relatives = Normalize(paths);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            progress?.Report($"Downloading {PathList.Format(relatives)}…");
            await DownloadCoreAsync(relatives, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadCoreAsync(IReadOnlyCollection<string> relatives, CancellationToken ct)
    {
        if (relatives.Count == 0) return;
        // --include on the command line overrides lfs.fetchinclude; an empty --exclude clears any exclude rule.
        await RunCheckedAsync("download files", new[] { "lfs", "pull", "--include", string.Join(",", relatives), "--exclude", "" }, ct, TransferTimeout).ConfigureAwait(false);
    }

    private static IReadOnlyList<string>? ParseDownloadFolders(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value!.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.EndsWith("/**", StringComparison.Ordinal) && !p.StartsWith(".pdm", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Substring(0, p.Length - 3))
            .ToList();
    }

    // ---------------------------------------------------------------- Milestones

    /// <summary>Milestones, newest first.</summary>
    public async Task<IReadOnlyList<Milestone>> GetMilestonesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await FetchAsync(ct).ConfigureAwait(false);
            }
            catch (VaultOfflineException)
            {
                // Show the milestones this PC already knows about.
            }
            return await ListMilestonesCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Admin: labels the vault's latest state, e.g. "Design Review 1", so anyone can view it later.</summary>
    public async Task<Milestone> CreateMilestoneAsync(string name, string description, CancellationToken ct = default)
    {
        RequireAdmin("create milestones");
        name = (name ?? "").Trim();
        description = (description ?? "").Trim();
        if (name.Length == 0) throw new PdmException("Give the milestone a name, for example \"Design Review 1\".");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FetchAsync(ct).ConfigureAwait(false);
            if (!await UpstreamExistsAsync(ct).ConfigureAwait(false)) throw new PdmException("The vault is empty, so there's nothing to label yet.");

            var tag = MilestoneTags.TagFor(name);
            if ((await RunAsync(new[] { "rev-parse", "--verify", "--quiet", "refs/tags/" + tag }, ct).ConfigureAwait(false)).Success)
            {
                throw new PdmException($"There's already a milestone called “{name}”. Pick a different name.");
            }

            var message = description.Length > 0 ? name + "\n\n" + description : name;
            await RunCheckedAsync("create the milestone", new[] { "tag", "-a", tag, "-m", message, Upstream }, ct).ConfigureAwait(false);
            var push = await RunAsync(new[] { "push", Config.Remote, "refs/tags/" + tag }, ct, NetworkTimeout).ConfigureAwait(false);
            if (!push.Success)
            {
                await RunAsync(new[] { "tag", "-d", tag }, ct).ConfigureAwait(false);
                throw GitErrorClassifier.Classify(push, "create the milestone");
            }

            await NotifyAsync(new VaultEvent(VaultEventKind.MilestoneCreated, UserDisplayName, new[] { name }) { Comment = description }, new OperationResult()).ConfigureAwait(false);
            return new Milestone(tag, name, description, UserName, DateTimeOffset.Now);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Switches this PC's vault folder to show every file as it was at the milestone. Read-only:
    /// nothing can be checked out or in until <see cref="ReturnToLatestAsync"/>.
    /// </summary>
    public async Task ViewMilestoneAsync(string tag, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var changed = (await GetLocalChangesAsync(ct).ConfigureAwait(false)).Where(c => !c.IsUntracked).Select(c => c.Path).ToList();
            if (changed.Count > 0)
            {
                throw new PdmException($"You have changes on this PC that aren't checked in ({PathList.Format(changed)}). " +
                                       "Check them in (or undo them) before viewing a milestone, so nothing gets mixed up.");
            }
            await EnsureFilesFreeAsync("HEAD", tag, ct).ConfigureAwait(false);

            progress?.Report("Getting the milestone's files…");
            await RunCheckedAsync("view the milestone", new[] { "checkout", "--quiet", "--detach", tag }, ct, TransferTimeout).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Leaves a milestone view and brings the vault folder back to the newest files.</summary>
    public async Task<GetLatestResult> ReturnToLatestAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await GetViewingMilestoneAsync(ct).ConfigureAwait(false) != null)
            {
                await EnsureFilesFreeAsync("HEAD", Config.Branch, ct).ConfigureAwait(false);
                progress?.Report("Returning to the latest files…");
                await RunCheckedAsync("return to the latest files", new[] { "checkout", "--quiet", Config.Branch }, ct, TransferTimeout).ConfigureAwait(false);
            }
            return await SyncCoreAsync(progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<Milestone>> ListMilestonesCoreAsync(CancellationToken ct)
    {
        var r = await RunAsync(new[] { "for-each-ref", "--sort=-creatordate", "--format=" + MilestoneTags.ListFormat, "refs/tags/" + MilestoneTags.Prefix }, ct).ConfigureAwait(false);
        var milestones = r.Success ? MilestoneTags.Parse(r.StdOut) : new List<Milestone>();
        if (!await UpstreamExistsAsync(ct).ConfigureAwait(false)) return milestones;

        // Only milestones on the vault's real history count. A tag on a side commit (made with Git
        // commands) could carry altered files or a disabled guard, so it's ignored.
        var genuine = new List<Milestone>();
        foreach (var milestone in milestones)
        {
            var onHistory = await RunAsync(new[] { "merge-base", "--is-ancestor", milestone.Tag + "^{commit}", Upstream }, ct).ConfigureAwait(false);
            if (onHistory.Success) genuine.Add(milestone);
        }
        return genuine;
    }

    /// <summary>The milestone this PC is showing, or null when it's on the latest files.</summary>
    private async Task<Milestone?> GetViewingMilestoneAsync(CancellationToken ct)
    {
        if ((await RunAsync(new[] { "symbolic-ref", "--quiet", "HEAD" }, ct).ConfigureAwait(false)).Success) return null;

        var describe = await RunAsync(new[] { "describe", "--tags", "--exact-match", "--match", MilestoneTags.Prefix + "*", "HEAD" }, ct).ConfigureAwait(false);
        var tag = describe.Success ? describe.StdOut.Trim() : "";
        if (tag.Length == 0) return new Milestone("", "an older version", "", "", default);
        var known = (await ListMilestonesCoreAsync(ct).ConfigureAwait(false)).FirstOrDefault(m => m.Tag == tag);
        return known ?? new Milestone(tag, tag.Substring(MilestoneTags.Prefix.Length), "", "", default);
    }

    private async Task EnsureNotViewingMilestoneAsync(CancellationToken ct)
    {
        var milestone = await GetViewingMilestoneAsync(ct).ConfigureAwait(false);
        if (milestone != null) throw new ViewingMilestoneException(milestone.Name);
    }

    /// <summary>Files that differ between two versions get replaced on disk, so none may be open in SolidWorks.</summary>
    private async Task EnsureFilesFreeAsync(string from, string to, CancellationToken ct)
    {
        var diff = await RunAsync(new[] { "diff", "--name-only", "-z", "--no-renames", from, to }, ct).ConfigureAwait(false);
        if (!diff.Success) return;
        var inUse = GitParsers.SplitZ(diff.StdOut).Where(p => FileUtil.IsInUse(ToFullPath(p))).ToList();
        if (inUse.Count > 0) throw new FileInUseException(inUse, "Close them in SolidWorks, then try again.");
    }

    private void RequireAdmin(string what)
    {
        if (IsAdmin) return;
        throw new PdmException($"Only vault admins can {what}. GitHub decides who's an admin (repo Admin or Maintain role); you're signed in as " +
                               (GitHubLogin ?? "not verified yet (are you online and signed in to GitHub?)") + ".");
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
        RunCheckedAsync("contact the vault", new[] { "fetch", "--prune", "--tags", Config.Remote }, ct, NetworkTimeout);

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

    /// <summary>Per file: check-ins in the vault that this PC doesn't have yet.</summary>
    private async Task<Dictionary<string, int>> GetIncomingCheckInCountsAsync(CancellationToken ct)
    {
        if (!await UpstreamExistsAsync(ct).ConfigureAwait(false)) return new Dictionary<string, int>();
        var log = await RunAsync(new[] { "log", "--no-merges", "--name-only", "--no-renames", "--format=%x1e", "HEAD.." + Upstream }, ct).ConfigureAwait(false);
        return log.Success ? GitParsers.CountCommitsPerFile(log.StdOut) : new Dictionary<string, int>();
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
