using System;
using System.Collections.Generic;
using Jarvis.Core.Errors;

namespace Jarvis.Core.Vault;

public enum FileState
{
    /// <summary>In the vault, nobody has it checked out, no local changes.</summary>
    Available,
    CheckedOutByMe,
    CheckedOutByOther,
    /// <summary>A non-lockable file (e.g. pdm.json, .xlsx) with local edits. Can be checked in directly.</summary>
    Modified,
    /// <summary>A lockable file edited without checking it out first. Needs attention.</summary>
    ModifiedWithoutCheckOut,
    /// <summary>New on this PC, never checked in.</summary>
    NotInVault,
    /// <summary>In the vault but deleted from this PC.</summary>
    MissingLocally,
    /// <summary>An admin froze it (e.g. released for production). Nobody can check it out until an admin unfreezes it.</summary>
    Frozen,
    /// <summary>Submitted for review before release. Its submitter holds it until an admin approves (releases) or rejects it.</summary>
    InReview,
}

public sealed class LockInfo
{
    public LockInfo(string id, string path, string owner, DateTimeOffset? lockedAt, bool isMine)
    {
        Id = id;
        Path = path;
        Owner = owner;
        LockedAt = lockedAt;
        IsMine = isMine;
    }

    public string Id { get; }
    public string Path { get; }
    /// <summary>GitHub username of the lock holder.</summary>
    public string Owner { get; }
    public DateTimeOffset? LockedAt { get; }
    public bool IsMine { get; }
}

/// <summary>One check-in of one file.</summary>
public sealed class FileVersion
{
    public FileVersion(string commitId, string author, string authorEmail, DateTimeOffset date, string comment)
    {
        CommitId = commitId;
        Author = author;
        AuthorEmail = authorEmail;
        Date = date;
        Comment = comment;
    }

    /// <summary>1 for the first check-in of the file, counting up.</summary>
    public int Version { get; set; }
    public string CommitId { get; }
    public string ShortId => CommitId.Length > 7 ? CommitId.Substring(0, 7) : CommitId;
    public string Author { get; }
    public string AuthorEmail { get; }
    public DateTimeOffset Date { get; }
    public string Comment { get; }

    public string CommentFirstLine
    {
        get
        {
            var i = Comment.IndexOf('\n');
            return (i >= 0 ? Comment.Substring(0, i) : Comment).Trim();
        }
    }
}

public sealed class VaultFile
{
    /// <summary>Path relative to the vault root with forward slashes, as Git reports it.</summary>
    public string RelativePath { get; set; } = "";
    public string Name => PathList.Name(RelativePath);

    public string Folder
    {
        get
        {
            var i = RelativePath.LastIndexOf('/');
            return i >= 0 ? RelativePath.Substring(0, i) : "";
        }
    }

    public FileState State { get; set; }
    public bool IsLockable { get; set; }
    public LockInfo? Lock { get; set; }
    /// <summary>Display name of whoever has it checked out.</summary>
    public string? CheckedOutBy { get; set; }
    public bool HasLocalChanges { get; set; }
    /// <summary>The vault has a newer version than this PC.</summary>
    public bool IsOutdated { get; set; }
    public bool ExistsLocally { get; set; }
    public FileVersion? LatestVersion { get; set; }
    /// <summary>Check-ins of this file on this PC (the "7" in 7/10).</summary>
    public int Version => PinnedVersion?.Version ?? LatestVersion?.Version ?? 0;
    /// <summary>Check-ins of this file in the vault, including ones this PC hasn't got yet (the "10" in 7/10).</summary>
    public int VaultVersion { get; set; }
    /// <summary>"7/10" style text; empty for files never checked in.</summary>
    public string VersionText => VaultVersion == 0 ? "" : $"{Version}/{VaultVersion}";

    /// <summary>Set when an admin froze the file.</summary>
    public FrozenEntry? Freeze { get; set; }
    /// <summary>Display name of the admin who froze it.</summary>
    public string? FrozenBy { get; set; }
    /// <summary>Latest released revision letter (given when frozen), kept after unfreezing. Null if never released.</summary>
    public string? Revision { get; set; }
    /// <summary>Frozen, but the admin's lock is gone (e.g. broken), so the freeze isn't enforced. Freeze it again.</summary>
    public bool FreezeLockMissing { get; set; }

    /// <summary>An admin asked whoever has it checked out to check it in.</summary>
    public AdminCheckInRequest? CheckInRequest { get; set; }
    /// <summary>Display name of the admin who asked.</summary>
    public string? CheckInRequestedBy { get; set; }

    /// <summary>References, properties and saved-in version recorded by the SolidWorks add-in (null if not indexed).</summary>
    public IndexEntry? Index { get; set; }
    /// <summary>Files that reference this one (from the index): the assemblies and drawings that would be affected by changing it.</summary>
    public IReadOnlyList<string> UsedIn { get; set; } = Array.Empty<string>();

    /// <summary>Only a small placeholder is on this PC (its folder isn't in "only download my folders"). Download it before opening.</summary>
    public bool NotDownloaded { get; set; }

    /// <summary>Set when this PC uses an older version of the file (this PC only; the vault keeps the latest).</summary>
    public PinnedVersion? PinnedVersion { get; set; }
    /// <summary>A read-only copy of an older version of another file (see <see cref="VersionCopies"/>).</summary>
    public bool IsVersionCopy => VersionCopies.IsVersionCopy(RelativePath);

    /// <summary>The latest version this PC has (what "Return to latest" goes back to).</summary>
    public int LatestLocalVersion => LatestVersion?.Version ?? 0;

    /// <summary>Set while the file is waiting for review.</summary>
    public ReviewEntry? Review { get; set; }
    /// <summary>Display name of whoever submitted it for review.</summary>
    public string? ReviewSubmittedBy { get; set; }

    /// <summary>Drawings that show this part/assembly (from the index).</summary>
    public IReadOnlyList<string> Drawings { get; set; } = Array.Empty<string>();
    /// <summary>For a drawing: models checked in after the drawing's last check-in, so it may be out of date.</summary>
    public IReadOnlyList<string> ChangedSinceDrawing { get; set; } = Array.Empty<string>();
    /// <summary>For a part/assembly: its drawings that haven't been checked in since it changed.</summary>
    public IReadOnlyList<string> StaleDrawings { get; set; } = Array.Empty<string>();
}

public sealed class VaultSnapshot
{
    public IReadOnlyList<VaultFile> Files { get; set; } = Array.Empty<VaultFile>();
    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>True when GitHub couldn't be reached; lock info is then the last known.</summary>
    public bool IsOffline { get; set; }
    /// <summary>Why the vault couldn't be contacted (offline, sign-in, lock server error).</summary>
    public string? ConnectionProblem { get; set; }
    /// <summary>Lock info was never retrieved this session, so check-out states are unknown.</summary>
    public bool LocksUnknown { get; set; }

    /// <summary>Local check-ins that haven't been pushed to the vault yet.</summary>
    public int PendingCheckIns { get; set; }
    /// <summary>Check-ins in the vault that this PC hasn't got yet.</summary>
    public int UpdatesAvailable { get; set; }

    public string? ConfigProblem { get; set; }

    /// <summary>Admin check-in requests for files checked out to this user. Call ProcessCheckInRequestsAsync.</summary>
    public int CheckInRequestsForMe { get; set; }

    /// <summary>Set while this PC shows a milestone instead of the latest files (read-only).</summary>
    public Milestone? ViewingMilestone { get; set; }

    /// <summary>For admins: vault guard missing or changed, storage rules missing. Fixed by Update Vault Setup.</summary>
    public IReadOnlyList<string> SetupProblems { get; set; } = Array.Empty<string>();

    /// <summary>Folders this PC downloads; null means everything.</summary>
    public IReadOnlyList<string>? DownloadFolders { get; set; }
}

public sealed class FileFailure
{
    public FileFailure(string path, PdmException error)
    {
        Path = path;
        Error = error;
    }

    public string Path { get; }
    public PdmException Error { get; }
}

/// <summary>Outcome of a multi-file command. Some files can succeed while others fail.</summary>
public sealed class OperationResult
{
    public List<string> Succeeded { get; } = new List<string>();
    /// <summary>Informational messages, e.g. where a backup was saved.</summary>
    public List<string> Notes { get; } = new List<string>();
    /// <summary>The command worked, but something needs the user's attention.</summary>
    public List<string> Warnings { get; } = new List<string>();
    public List<FileFailure> Failures { get; } = new List<FileFailure>();

    public void Fail(string path, PdmException error) => Failures.Add(new FileFailure(path, error));
}

public sealed class GetLatestResult
{
    public static readonly GetLatestResult UpToDate = new GetLatestResult(0, Array.Empty<string>());

    public GetLatestResult(int checkInsReceived, IReadOnlyList<string> updatedFiles)
    {
        CheckInsReceived = checkInsReceived;
        UpdatedFiles = updatedFiles;
    }

    public int CheckInsReceived { get; }
    public IReadOnlyList<string> UpdatedFiles { get; }
}
