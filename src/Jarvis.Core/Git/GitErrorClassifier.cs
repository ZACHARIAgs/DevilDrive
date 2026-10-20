using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jarvis.Core.Errors;

namespace Jarvis.Core.Git;

/// <summary>Turns a failed Git command into a typed, user-readable <see cref="PdmException"/>.</summary>
public static class GitErrorClassifier
{
    private static readonly string[] StorageMarkers =
    {
        "exceeds GitHub's file size limit", "GH001", "over its data quota", "exceeded its LFS budget",
        "This repository exceeded", "storage quota", "data quota",
    };

    private static readonly string[] LockedPushMarkers =
    {
        "Unable to push locked files", "Cannot update locked files",
    };

    private static readonly string[] AuthMarkers =
    {
        "Authentication failed", "could not read Username", "could not read Password", "terminal prompts disabled",
        "Invalid username or password", "returned error: 401", "returned error: 403", "HTTP 401", "HTTP 403",
        "Permission denied (publickey)", "Repository not found", "Permission to ", "403 Forbidden",
        "must have push access", "must have admin access",
    };

    private static readonly string[] OfflineMarkers =
    {
        "Could not resolve host", "Failed to connect", "Connection timed out", "Connection refused",
        "Network is unreachable", "Could not read from remote repository", "unable to access", "dial tcp",
        "no such host", "i/o timeout", "connectex", "TLS handshake timeout", "context deadline exceeded",
        "Recv failure", "schannel: failed", "Operation timed out", "Connection reset", "getaddrinfo",
    };

    private static readonly string[] OverwriteMarkers =
    {
        "would be overwritten", "untracked working tree files",
    };

    private static readonly string[] InUseMarkers =
    {
        "unable to unlink", "unable to create file", "being used by another process", "Device or resource busy",
        "Access is denied", "Permission denied",
    };

    private static readonly string[] OutOfDateMarkers =
    {
        "[rejected]", "non-fast-forward", "fetch first", "Updates were rejected", "failed to push some refs",
    };

    private static readonly Regex InUsePath = new Regex(@"unable to (?:unlink old|create file) '?(?<p>[^':]+)'?", RegexOptions.Compiled);

    /// <param name="action">What the user was doing, as a verb phrase: "check in", "get latest".</param>
    public static PdmException Classify(GitResult result, string action)
    {
        var text = result.StdErr + "\n" + result.StdOut;
        var detail = result.Describe();

        if (Has(text, StorageMarkers))
        {
            return new PushRejectedException(PushRejectReason.StorageLimit,
                "The vault refused the files because a GitHub size or storage limit was reached. Tell an admin. They may need to free up LFS storage or buy more.",
                detail);
        }

        if (Has(text, LockedPushMarkers))
        {
            var files = LinesAfter(text, "Unable to push locked files")
                .Where(l => l.StartsWith("* ", StringComparison.Ordinal))
                .Select(l => l.Substring(2).Trim())
                .ToList();
            var list = files.Count > 0 ? "\n  " + string.Join("\n  ", files) : "";
            return new PushRejectedException(PushRejectReason.LockedByOthers,
                "The vault refused the check-in because it includes files checked out by someone else:" + list,
                detail);
        }

        if (Has(text, AuthMarkers)) return new VaultAuthException(action, detail);
        if (Has(text, OfflineMarkers)) return new VaultOfflineException(action, detail);

        if (Has(text, new[] { "index.lock" }))
        {
            return new GitCommandException(
                "Another Git operation is running in the vault (or one crashed). Close other Git tools, wait a moment and try again. " +
                "If it keeps happening, delete C:\\CapstoneVault\\.git\\index.lock.",
                detail);
        }

        if (Has(text, OverwriteMarkers))
        {
            var files = text.Split('\n').Where(l => l.StartsWith("\t", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
            return new VaultConflictException(files,
                $"Couldn't {action} because it would overwrite files on your PC that aren't in the vault yet or have local changes:\n  " +
                string.Join("\n  ", files.DefaultIfEmpty("(see details)")) +
                "\nMove or rename your copies, then try again.",
                detail);
        }

        if (Has(text, InUseMarkers))
        {
            var files = InUsePath.Matches(text).Cast<Match>().Select(m => m.Groups["p"].Value.Trim()).Distinct().ToList();
            return new FileInUseException(files.Count > 0 ? files : new List<string> { "A vault file" },
                $"Close it in SolidWorks (or the other program), then {action} again.");
        }

        if (Has(text, OutOfDateMarkers))
        {
            return new PushRejectedException(PushRejectReason.OutOfDate,
                "Someone else checked in at the same moment, and the vault refused your check-in.",
                detail);
        }

        return new GitCommandException($"Git reported a problem while trying to {action}: {FirstMeaningfulLine(text)}", detail);
    }

    internal static string FirstMeaningfulLine(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var line = lines.FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                   ?? lines.FirstOrDefault()
                   ?? "(no message)";
        var colon = line.IndexOf(':');
        if (colon > 0 && colon < 8) line = line.Substring(colon + 1).Trim();
        return line;
    }

    private static bool Has(string text, IEnumerable<string> markers) =>
        markers.Any(m => text.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);

    private static IEnumerable<string> LinesAfter(string text, string marker)
    {
        var seen = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (seen) yield return line;
            else if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) seen = true;
        }
    }
}
