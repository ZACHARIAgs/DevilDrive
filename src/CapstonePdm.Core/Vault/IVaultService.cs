using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CapstonePdm.Core.Config;

namespace CapstonePdm.Core.Vault;

/// <summary>
/// Every PDM command, with no UI. The WPF client uses it now; the Phase 2 SolidWorks add-in
/// calls the same methods with the active document's path.
/// Paths can be absolute (inside the vault) or vault-relative.
/// Methods throw <see cref="Errors.PdmException"/> subclasses with user-readable messages.
/// </summary>
public interface IVaultService
{
    string VaultRoot { get; }
    VaultConfig Config { get; }
    string UserDisplayName { get; }
    /// <summary>GitHub account verified with GitHub (null until verified).</summary>
    string? GitHubLogin { get; }
    bool IsAdmin { get; }

    Task<VaultSnapshot> GetSnapshotAsync(bool contactVault = true, CancellationToken ct = default);
    Task<VaultFile?> GetFileAsync(string path, bool contactVault = false, CancellationToken ct = default);

    Task<GetLatestResult> GetLatestAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<OperationResult> CheckOutAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<OperationResult> CheckInAsync(IEnumerable<string> paths, string comment, bool keepCheckedOut = false, IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, IndexEntry?>? indexUpdates = null, IReadOnlyList<string>? previews = null);
    Task<OperationResult> UndoCheckOutAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default);

    Task<IReadOnlyList<FileVersion>> GetHistoryAsync(string path, CancellationToken ct = default);
    Task<OperationResult> RestoreVersionAsync(string path, FileVersion version, string comment, IProgress<string>? progress = null, CancellationToken ct = default);

    Task<OperationResult> BreakLockAsync(string path, CancellationToken ct = default);

    /// <summary>Admin: freeze files (e.g. released for production) so nobody can change them.</summary>
    Task<OperationResult> FreezeAsync(IEnumerable<string> paths, string reason, IProgress<string>? progress = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? revisions = null);

    /// <summary>Revision letters these files would get if frozen now.</summary>
    Task<IReadOnlyDictionary<string, string>> GetNextRevisionsAsync(IEnumerable<string> paths, CancellationToken ct = default);

    /// <summary>Admin: make frozen files editable again.</summary>
    Task<OperationResult> UnfreezeAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Admin: ask whoever has a file checked out to check in their last saved version (done by their PC).</summary>
    Task<OperationResult> RequestCheckInAsync(string path, string note, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Carries out admin check-in requests for files checked out to this user: checks in the last saved
    /// version and releases the check-out. Clients call it when a snapshot reports CheckInRequestsForMe.
    /// </summary>
    Task<OperationResult> ProcessCheckInRequestsAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Saves index entries (references, properties, saved-in version) recorded by the SolidWorks add-in.</summary>
    Task<OperationResult> UpdateIndexAsync(IReadOnlyDictionary<string, IndexEntry?> entries, IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyList<string>? previews = null);

    /// <summary>Milestones (labeled snapshots of the whole vault), newest first.</summary>
    Task<IReadOnlyList<Milestone>> GetMilestonesAsync(CancellationToken ct = default);

    /// <summary>Admin: label the vault's latest state, e.g. "Design Review 1".</summary>
    Task<Milestone> CreateMilestoneAsync(string name, string description, CancellationToken ct = default);

    /// <summary>Show every file as it was at the milestone (read-only until ReturnToLatestAsync).</summary>
    Task ViewMilestoneAsync(string tag, IProgress<string>? progress = null, CancellationToken ct = default);

    Task<GetLatestResult> ReturnToLatestAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Admin: install or repair the GitHub vault guard and storage rules.</summary>
    Task<OperationResult> UpdateVaultSetupAsync(IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Git LFS storage used by every stored version (what counts against GitHub's free storage).</summary>
    Task<StorageUsage> GetStorageUsageAsync(CancellationToken ct = default);

    /// <summary>Message asking to confirm checking in new/changed export files (STEP, PDF, DXF...), or null.</summary>
    Task<string?> GetExportWarningAsync(IEnumerable<string> paths, CancellationToken ct = default);

    /// <summary>Folders this PC downloads (null = everything).</summary>
    Task<IReadOnlyList<string>?> GetDownloadFoldersAsync(CancellationToken ct = default);

    Task SetDownloadFoldersAsync(IReadOnlyList<string>? folders, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Downloads specific files even if their folder isn't chosen.</summary>
    Task DownloadAsync(IEnumerable<string> paths, IProgress<string>? progress = null, CancellationToken ct = default);

    string ToRelativePath(string path);
    string ToFullPath(string relativePath);
}
