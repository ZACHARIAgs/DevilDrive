using System;
using System.Collections.Generic;
using System.Linq;

namespace CapstonePdm.Core.Errors;

/// <summary>
/// Base for every error the vault reports. <see cref="Exception.Message"/> is written for the
/// user in PDM terms; <see cref="Detail"/> holds the raw Git output for troubleshooting.
/// </summary>
public class PdmException : Exception
{
    public PdmException(string message, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Detail = detail;
    }

    public string? Detail { get; }

    public virtual string Title => "Vault error";
}

public sealed class VaultOfflineException : PdmException
{
    public VaultOfflineException(string action, string? detail = null)
        : base($"Can't reach the vault on GitHub, so Capstone PDM couldn't {action}. Check your internet connection and try again.", detail)
    {
    }

    public override string Title => "Vault offline";
}

public sealed class VaultAuthException : PdmException
{
    public VaultAuthException(string action, string? detail = null)
        : base($"GitHub refused the request to {action}. You may need to sign in again, or your GitHub account doesn't have permission for the vault repo. Ask an admin to check you're a collaborator.", detail)
    {
    }

    public override string Title => "Sign-in or permission problem";
}

public sealed class LockHeldException : PdmException
{
    public LockHeldException(string path, string owner, DateTimeOffset? lockedAt)
        : base(BuildMessage(path, owner, lockedAt))
    {
        Path = path;
        Owner = owner;
        LockedAt = lockedAt;
    }

    public string Path { get; }
    public string Owner { get; }
    public DateTimeOffset? LockedAt { get; }

    public override string Title => "Checked out by someone else";

    private static string BuildMessage(string path, string owner, DateTimeOffset? lockedAt)
    {
        var since = lockedAt.HasValue ? $" since {lockedAt.Value.LocalDateTime:ddd MMM d, h:mm tt}" : "";
        return $"{PathList.Name(path)} is checked out by {owner}{since}. Ask them to check it in, or ask an admin to break the check-out if it's stale.";
    }
}

public sealed class FrozenFileException : PdmException
{
    public FrozenFileException(string path, string frozenBy, DateTimeOffset frozenAt, string reason, bool userIsAdmin)
        : base($"{PathList.Name(path)} is frozen by {frozenBy} (since {frozenAt.LocalDateTime:MMM d, yyyy})" +
               (string.IsNullOrWhiteSpace(reason) ? "." : $": “{reason}”.") +
               (userIsAdmin
                   ? " Frozen files can't be changed. If it really needs to change, Unfreeze it first."
                   : " Frozen files can't be changed. Ask an admin if it needs to change."))
    {
        Path = path;
    }

    public string Path { get; }

    public override string Title => "File is frozen";
}

public enum PushRejectReason
{
    OutOfDate,
    LockedByOthers,
    StorageLimit,
    Other,
}

public sealed class PushRejectedException : PdmException
{
    public PushRejectedException(PushRejectReason reason, string message, string? detail = null, Exception? inner = null)
        : base(message, detail, inner)
    {
        Reason = reason;
    }

    public PushRejectReason Reason { get; }

    public override string Title => "Check-in not sent to the vault";

    /// <summary>Wraps any failure that happened after the commit was made but before it reached GitHub.</summary>
    public static PushRejectedException NotSent(PdmException cause)
    {
        var reason = cause is PushRejectedException p ? p.Reason : PushRejectReason.Other;
        return new PushRejectedException(
            reason,
            cause.Message + "\n\nYour new version is saved on this PC but has NOT reached the vault yet, and your files are still checked out to you. " +
            "Fix the problem above, then click Check In again. Nothing will be lost.",
            cause.Detail,
            cause);
    }
}

public sealed class FileInUseException : PdmException
{
    public FileInUseException(IReadOnlyList<string> paths, string hint)
        : base($"{PathList.Format(paths)} {(paths.Count == 1 ? "is" : "are")} open in another program (probably SolidWorks). {hint}")
    {
        Paths = paths;
    }

    public IReadOnlyList<string> Paths { get; }

    public override string Title => "File in use";
}

public sealed class NotCheckedOutException : PdmException
{
    public NotCheckedOutException(IReadOnlyList<string> paths)
        : base($"{PathList.Format(paths)} {(paths.Count == 1 ? "isn't" : "aren't")} checked out to you, so {(paths.Count == 1 ? "it" : "they")} can't be checked in. " +
               "Your edits are still on this PC: check the file out first (your edits are kept), then check in.")
    {
        Paths = paths;
    }

    public IReadOnlyList<string> Paths { get; }

    public override string Title => "Not checked out";
}

public sealed class VaultConflictException : PdmException
{
    public VaultConflictException(IReadOnlyList<string> paths, string message, string? detail = null)
        : base(message, detail)
    {
        Paths = paths;
    }

    public IReadOnlyList<string> Paths { get; }

    public override string Title => "Conflict";
}

public sealed class GitNotFoundException : PdmException
{
    public GitNotFoundException()
        : base("Git isn't installed on this PC (or can't be found). Run Setup.cmd, then restart Capstone PDM.")
    {
    }

    public override string Title => "Git not found";
}

public sealed class VaultNotFoundException : PdmException
{
    public VaultNotFoundException(string path)
        : base($"No vault found at {path}. Run Setup.cmd to download the vault, or choose the right folder in Settings.")
    {
    }

    public override string Title => "Vault not found";
}

public sealed class GitCommandException : PdmException
{
    public GitCommandException(string message, string? detail = null)
        : base(message, detail)
    {
    }

    public override string Title => "Git error";
}

public static class PathList
{
    public static string Name(string path)
    {
        var i = path.LastIndexOfAny(new[] { '/', '\\' });
        return i >= 0 ? path.Substring(i + 1) : path;
    }

    /// <summary>"a.sldprt", "a.sldprt and b.sldprt", "a, b, c and 4 more".</summary>
    public static string Format(IEnumerable<string> paths, int max = 5)
    {
        var names = paths.Select(Name).ToList();
        if (names.Count == 0) return "(no files)";
        if (names.Count == 1) return names[0];
        if (names.Count <= max) return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
        return string.Join(", ", names.Take(max)) + $" and {names.Count - max} more";
    }
}
