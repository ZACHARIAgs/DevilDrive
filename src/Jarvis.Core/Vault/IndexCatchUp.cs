using System;
using System.Collections.Generic;
using System.Linq;

namespace Jarvis.Core.Vault;

/// <summary>
/// Keeps the vault index complete without anyone running anything: every check-in from SolidWorks also
/// records a few other files whose record is missing or out of date (checked in from the desktop app,
/// which can't read SolidWorks files, or from before the add-in), or that have no 3D preview yet.
/// </summary>
public static class IndexCatchUp
{
    /// <summary>At most this many extra files per check-in, so a check-in never takes long; the rest go next time.</summary>
    public const int PerCheckIn = 10;

    /// <summary>
    /// A record written in the same check-in as its file is a few seconds older than that check-in; anything
    /// later than this means the file was checked in again without updating its record.
    /// </summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Files this PC can record now, most useful first (no record at all, then out of date, then just a missing
    /// 3D preview). Only real, unchanged copies of the vault version: changed, older-version and not-downloaded
    /// files would give a wrong record. <paramref name="hasPreview"/> says whether a file's 3D preview exists.
    /// </summary>
    public static IReadOnlyList<VaultFile> FilesToRecord(IEnumerable<VaultFile> files, Func<string, bool> hasPreview, ICollection<string>? skip = null)
    {
        return files
            .Where(f => VaultService.IsSolidWorksFile(f.RelativePath) && !f.IsVersionCopy)
            .Where(f => f.State is FileState.Available or FileState.CheckedOutByMe or FileState.CheckedOutByOther or FileState.Frozen or FileState.InReview)
            .Where(f => f.ExistsLocally && !f.NotDownloaded && !f.HasLocalChanges && f.PinnedVersion == null && !f.IsOutdated)
            .Where(f => skip == null || !skip.Contains(f.RelativePath))
            .Select(f => (File: f, Need: Need(f, hasPreview)))
            .Where(x => x.Need > 0)
            .OrderByDescending(x => x.Need)
            .ThenBy(x => x.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.File)
            .ToList();
    }

    /// <summary>3 = no record, 2 = record older than the last check-in, 1 = no 3D preview, 0 = nothing to do.</summary>
    private static int Need(VaultFile file, Func<string, bool> hasPreview)
    {
        if (file.Index == null) return 3;
        if (file.LatestVersion != null && file.LatestVersion.Date > file.Index.UpdatedAt + Slack) return 2;
        var canHavePreview = !file.RelativePath.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase);
        return canHavePreview && !hasPreview(VaultPaths.PreviewFor(file.RelativePath)) ? 1 : 0;
    }
}
