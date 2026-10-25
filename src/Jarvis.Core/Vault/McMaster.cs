using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Vault;

/// <summary>
/// Adding McMaster-Carr parts: Jarvis opens the part's McMaster page, the user clicks McMaster's own Download
/// button (their API needs an approved account and certificate, and downloading for them would break their
/// terms), and Jarvis picks the file up from Downloads and files it under COTS/MCMASTER in the vault.
/// </summary>
public static class McMaster
{
    /// <summary>Where McMaster parts go in the vault.</summary>
    public const string Folder = "COTS/MCMASTER";

    /// <summary>File types worth keeping from a McMaster download (SolidWorks first, STEP if that's what was chosen).</summary>
    public static readonly string[] Extensions = { ".sldprt", ".sldasm", ".step", ".stp" };

    // McMaster part numbers: digits, one or two letters, digits (5709K91, 91251A540, 6655K14, 8975K41).
    private static readonly Regex PartNumberPattern = new Regex(@"^\d{2,6}[A-Za-z]{1,2}\d{1,5}$", RegexOptions.Compiled);

    /// <summary>The part number as McMaster writes it (upper case, no spaces), or null if it doesn't look like one.</summary>
    public static string? Normalize(string? text)
    {
        var trimmed = Regex.Replace(text ?? "", @"\s+", "").Trim().ToUpperInvariant();
        return PartNumberPattern.IsMatch(trimmed) ? trimmed : null;
    }

    public static string PageUrl(string partNumber) => $"https://www.mcmaster.com/{Uri.EscapeDataString(partNumber)}/";

    /// <summary>
    /// The finished download for this part number among the files in Downloads: its name contains the part number,
    /// it's a CAD file (not a browser's half-done .crdownload/.part), and it appeared after <paramref name="since"/>.
    /// SolidWorks files win over STEP; the newest wins after that. Null if it isn't there yet.
    /// </summary>
    public static string? FindDownload(IEnumerable<(string Path, DateTime LastWriteUtc)> files, string partNumber, DateTime sinceUtc)
    {
        return files
            .Where(f => f.LastWriteUtc >= sinceUtc.AddSeconds(-2))
            .Where(f => Path.GetFileName(f.Path).IndexOf(partNumber, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(f => (f.Path, f.LastWriteUtc, Rank: Array.IndexOf(Extensions, Path.GetExtension(f.Path).ToLowerInvariant())))
            .Where(f => f.Rank >= 0)
            .OrderBy(f => f.Rank < 2 ? 0 : 1)
            .ThenByDescending(f => f.LastWriteUtc)
            .Select(f => f.Path)
            .FirstOrDefault();
    }

    /// <summary>Vault-relative path a downloaded file goes to: COTS/MCMASTER/&lt;McMaster's file name&gt;.</summary>
    public static string VaultPathFor(string downloadPath) => Folder + "/" + Path.GetFileName(downloadPath);
}
