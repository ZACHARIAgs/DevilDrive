using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CapstonePdm.Core.Vault;

/// <summary>
/// Read-only copies of older versions, so single instances in an assembly can use them while the
/// original file (and every other instance and assembly) stays on its own version. A copy of version
/// 3 of "frame/bracket.SLDPRT" is "frame/_versions/bracket v3.SLDPRT". Its contents are identical
/// to that version, so Git LFS stores nothing extra.
/// </summary>
public static class VersionCopies
{
    public const string FolderName = "_versions";
    private static readonly Regex CopyName = new Regex(@"^(?<base>.+) v(?<version>\d+)$");

    /// <summary>Vault-relative path (forward slashes) of the copy of a version of a file.</summary>
    public static string CopyPathFor(string originalRelative, int version)
    {
        var rel = originalRelative.Replace('\\', '/');
        var slash = rel.LastIndexOf('/');
        var folder = slash >= 0 ? rel.Substring(0, slash + 1) : "";
        var name = rel.Substring(slash + 1);
        return $"{folder}{FolderName}/{Path.GetFileNameWithoutExtension(name)} v{version}{Path.GetExtension(name)}";
    }

    public static bool IsVersionCopy(string path) => Parse(path) != null;

    /// <summary>The version a copy holds, or null if the path isn't a version copy.</summary>
    public static int? VersionOf(string path) => Parse(path)?.Version;

    /// <summary>The original file a copy was made from (same style of path as given), or null.</summary>
    public static string? OriginalOf(string path) => Parse(path)?.Original;

    private static (string Original, int Version)? Parse(string path)
    {
        var separator = path.Contains("\\") ? '\\' : '/';
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Length < 2 || !string.Equals(parts[parts.Length - 2], FolderName, StringComparison.OrdinalIgnoreCase)) return null;
        var name = parts[parts.Length - 1];
        var match = CopyName.Match(Path.GetFileNameWithoutExtension(name));
        if (!match.Success || !int.TryParse(match.Groups["version"].Value, out var version)) return null;
        var folder = string.Join(separator.ToString(), parts, 0, parts.Length - 2);
        var original = (folder.Length > 0 ? folder + separator : "") + match.Groups["base"].Value + Path.GetExtension(name);
        return (original, version);
    }
}
