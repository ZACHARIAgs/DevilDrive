using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Errors;

namespace Jarvis.Core.Vault;

/// <summary>How much of GitHub's file storage this vault's files and their whole history take up.</summary>
public sealed class StorageUsage
{
    public StorageUsage(long bytes, int versions, double limitGb)
    {
        Bytes = bytes;
        Versions = versions;
        LimitGb = limitGb;
    }

    public long Bytes { get; }
    /// <summary>How many stored file versions that is (every check-in of a CAD file keeps one).</summary>
    public int Versions { get; }
    /// <summary>The team's storage allowance (pdm.json "storageLimitGb").</summary>
    public double LimitGb { get; }
    public double Gb => Bytes / 1073741824.0;
    /// <summary>0 to 1 (and beyond, once over the allowance).</summary>
    public double Fraction => LimitGb > 0 ? Gb / LimitGb : 0;
    /// <summary>Worth warning about: four fifths of the allowance is gone.</summary>
    public bool IsNearlyFull => Fraction >= 0.8;

    /// <summary>"1.2 of 10 GB" (MB below 1 GB).</summary>
    public string Text => (Gb >= 1 ? Gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB" : (Bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB") +
                          " of " + LimitGb.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
}

/// <summary>One check-in in the vault, for the "what changed" list.</summary>
public sealed class RecentCheckIn
{
    public RecentCheckIn(FileVersion version, IReadOnlyList<string> files, bool isNew, bool isMine)
    {
        Version = version;
        Files = files;
        IsNew = isNew;
        IsMine = isMine;
    }

    public FileVersion Version { get; }
    /// <summary>The files it changed (vault-relative), without Jarvis's own records.</summary>
    public IReadOnlyList<string> Files { get; }
    /// <summary>Checked in since this PC's user last marked the list as seen.</summary>
    public bool IsNew { get; }
    public bool IsMine { get; }
}

public sealed partial class VaultService
{
    private const string LastSeenKey = "pdm.lastSeen";
    private const string StorageKey = "pdm.storage";

    // ---------------------------------------------------------------- Storage used

    /// <summary>
    /// How much storage the vault's files and all their earlier versions use, counted from the vault's history
    /// (each stored version once). Slow the first time on a long history, so the answer is remembered until the
    /// vault changes. Null if it can't be worked out (no Git LFS on this PC).
    /// </summary>
    public async Task<StorageUsage?> GetStorageUsageAsync(CancellationToken ct = default)
    {
        var limit = Config.StorageLimitGb > 0 ? Config.StorageLimitGb : Jarvis.Core.Config.VaultConfig.DefaultStorageLimitGb;
        var tip = await RunAsync(new[] { "rev-parse", "--verify", "--quiet", Upstream + "^{commit}" }, ct).ConfigureAwait(false);
        var commit = tip.Success ? tip.StdOut.Trim() : "";
        if (commit.Length > 0)
        {
            // "commit bytes versions", written the last time it was counted.
            var saved = (await GetConfigValueAsync(StorageKey, ct).ConfigureAwait(false) ?? "").Split(' ');
            if (saved.Length == 3 && saved[0] == commit && long.TryParse(saved[1], out var savedBytes) && int.TryParse(saved[2], out var savedVersions))
            {
                return new StorageUsage(savedBytes, savedVersions, limit);
            }
        }

        var list = await RunAsync(new[] { "lfs", "ls-files", "--all", "--json" }, ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        if (!list.Success) return null;
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var file in LfsFile.Parse(list.StdOut)) sizes[file.Oid] = file.Size;
        var usage = new StorageUsage(sizes.Values.Sum(), sizes.Count, limit);
        if (commit.Length > 0)
        {
            await RunAsync(new[] { "config", "--local", StorageKey, $"{commit} {usage.Bytes} {usage.Versions}" }, ct).ConfigureAwait(false);
        }
        return usage;
    }

    // ---------------------------------------------------------------- What changed

    /// <summary>
    /// The vault's newest check-ins, newest first, each marked new if it arrived since <see cref="MarkCheckInsSeenAsync"/>
    /// was last called on this PC. Check-ins that only touch Jarvis's own records (index, previews) are left out.
    /// </summary>
    public async Task<IReadOnlyList<RecentCheckIn>> GetRecentCheckInsAsync(int max = 40, CancellationToken ct = default)
    {
        if (!await UpstreamExistsAsync(ct).ConfigureAwait(false)) return Array.Empty<RecentCheckIn>();

        var lastSeen = await GetConfigValueAsync(LastSeenKey, ct).ConfigureAwait(false);
        var unseen = new HashSet<string>(StringComparer.Ordinal);
        if (lastSeen == null)
        {
            // First time on this PC: everything already here counts as seen.
            await MarkCheckInsSeenAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var range = await RunAsync(new[] { "rev-list", "--no-merges", lastSeen + ".." + Upstream }, ct).ConfigureAwait(false);
            // A marker that isn't in the history any more (rewritten): nothing can be called new.
            if (range.Success) unseen.UnionWith(range.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        }

        // Ask for more than max: some are Jarvis's own housekeeping and get dropped.
        var log = await RunAsync(new[] { "log", "--no-merges", "--name-only", "--no-renames", "-n", (max * 3).ToString(CultureInfo.InvariantCulture),
            "--format=" + GitParsers.HeaderFormat + "%s", Upstream }, ct).ConfigureAwait(false);
        if (!log.Success) return Array.Empty<RecentCheckIn>();
        var myEmail = await GetConfigValueAsync("user.email", ct).ConfigureAwait(false);
        return GitParsers.ParseRecentCheckIns(log.StdOut, unseen, UserName, myEmail).Take(max).ToList();
    }

    /// <summary>Remembers, on this PC, that the user has looked at everything in the vault up to now.</summary>
    public async Task MarkCheckInsSeenAsync(CancellationToken ct = default)
    {
        var tip = await RunAsync(new[] { "rev-parse", "--verify", "--quiet", Upstream + "^{commit}" }, ct).ConfigureAwait(false);
        if (tip.Success && tip.StdOut.Trim().Length > 0)
        {
            await RunAsync(new[] { "config", "--local", LastSeenKey, tip.StdOut.Trim() }, ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- Import: updating vault files

    /// <summary>
    /// Checks out the vault files an import is about to update with someone's newer copies (<see cref="ImportItem.ReplaceExisting"/>).
    /// All or nothing: if one can't be checked out (someone else has it, it's frozen, it's open in SolidWorks), the ones
    /// that were are released again and this throws, so the import stops before anything is copied.
    /// </summary>
    public async Task CheckOutForImportAsync(IReadOnlyList<string> targets, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (targets.Count == 0) return;
        var inUse = targets.Where(t => FileUtil.IsInUse(ToFullPath(t))).ToList();
        if (inUse.Count > 0) throw new FileInUseException(inUse, "The import updates them. Close them in SolidWorks, then import again.");

        var result = await CheckOutAsync(targets, progress, ct).ConfigureAwait(false);
        if (result.Failures.Count == 0) return;
        if (result.Succeeded.Count > 0) await UndoCheckOutAsync(result.Succeeded, progress, ct).ConfigureAwait(false);
        throw new PdmException("These vault files can't be updated right now, so nothing was imported:\n\n" +
                               string.Join("\n", result.Failures.Select(f => "\u2022 " + f.Error.Message)) +
                               "\n\nSort that out, or choose \"Use the vault's file\" for them, and import again.");
    }

    // ---------------------------------------------------------------- Conflicts

    /// <summary>
    /// The one-click way out of a Get Latest conflict (files changed on this PC that also have a newer version
    /// in the vault): keeps a copy of this PC's version of each file in the backups folder, puts the vault's
    /// version back, and gets latest. Nothing is lost; the user's versions are in the folder the notes name.
    /// </summary>
    public async Task<OperationResult> KeepCopiesAndGetLatestAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var relatives = Normalize(paths);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await UndoCheckOutCoreAsync(relatives, progress, ct).ConfigureAwait(false);
            if (result.Failures.Count > 0) return result; // e.g. open in SolidWorks: nothing more is done until that's sorted
            var latest = await SyncCoreAsync(progress, ct).ConfigureAwait(false);
            ReloadConfig();
            result.Notes.Insert(0, latest.CheckInsReceived == 0
                ? "The vault's versions are back in place."
                : $"The vault's versions are in place and {latest.UpdatedFiles.Count} file(s) were updated.");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
