using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CapstonePdm.Core.Vault;

internal sealed class StatusEntry
{
    public StatusEntry(char x, char y, string path)
    {
        X = x;
        Y = y;
        Path = path;
    }

    public char X { get; }
    public char Y { get; }
    public string Path { get; }
    public bool IsUntracked => X == '?';
    public bool IsDeleted => X == 'D' || Y == 'D';
}

/// <summary>Parsers for Git's machine-readable output. Pure functions, unit tested.</summary>
internal static class GitParsers
{
    private const char RecordSep = '\x1e';
    private const char FieldSep = '\x1f';

    /// <summary>log --format for a commit header: hash, author, email, ISO date, then a message placeholder.</summary>
    public const string HeaderFormat = "%x1e%H%x1f%an%x1f%ae%x1f%aI%x1f";

    public static List<string> SplitZ(string output) =>
        output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Parses <c>git status --porcelain=v1 -z</c>.</summary>
    public static List<StatusEntry> ParseStatus(string output)
    {
        var list = new List<StatusEntry>();
        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length < 4) continue;
            var x = part[0];
            var y = part[1];
            list.Add(new StatusEntry(x, y, part.Substring(3)));
            // Renames and copies are followed by the original path.
            if (x == 'R' || x == 'C') i++;
        }
        return list;
    }

    /// <summary>Parses <c>git check-attr -z</c>: path NUL attribute NUL value NUL ...</summary>
    public static List<(string Path, string Value)> ParseCheckAttr(string output)
    {
        var list = new List<(string, string)>();
        var parts = output.Split('\0');
        for (var i = 0; i + 2 < parts.Length; i += 3)
        {
            list.Add((parts[i], parts[i + 2]));
        }
        return list;
    }

    /// <summary>Parses <c>git lfs locks --verify --json</c>: {"ours":[...],"theirs":[...]}.</summary>
    public static List<LockInfo> ParseVerifiedLocks(string json)
    {
        var list = new List<LockInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        foreach (var (property, mine) in new[] { ("ours", true), ("theirs", false) })
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(property, out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                list.AddRange(array.EnumerateArray().Select(e => ReadLock(e, _ => mine)));
            }
        }
        return list;
    }

    /// <summary>Parses <c>git lfs locks --json</c>: a plain array; ownership decided by GitHub username.</summary>
    public static List<LockInfo> ParseLocks(string json, string? myLogin)
    {
        var list = new List<LockInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        list.AddRange(doc.RootElement.EnumerateArray().Select(e =>
            ReadLock(e, owner => myLogin != null && string.Equals(owner, myLogin, StringComparison.OrdinalIgnoreCase))));
        return list;
    }

    private static LockInfo ReadLock(JsonElement e, Func<string, bool> isMine)
    {
        var id = e.TryGetProperty("id", out var idEl) ? (idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText()) : "";
        var path = e.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : "";
        var owner = e.TryGetProperty("owner", out var ownerEl) && ownerEl.ValueKind == JsonValueKind.Object &&
                    ownerEl.TryGetProperty("name", out var nameEl)
            ? nameEl.GetString()
            : "";
        DateTimeOffset? lockedAt = null;
        if (e.TryGetProperty("locked_at", out var atEl) && atEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(atEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            lockedAt = at;
        }
        owner ??= "";
        return new LockInfo(id ?? "", path ?? "", owner, lockedAt, isMine(owner));
    }

    /// <summary>
    /// Parses <c>git log --format=HeaderFormat+%B</c> for one file, newest first, and numbers the
    /// versions so the oldest is version 1.
    /// </summary>
    public static List<FileVersion> ParseHistory(string output)
    {
        var versions = new List<FileVersion>();
        foreach (var record in output.Split(RecordSep))
        {
            var fields = record.Split(new[] { FieldSep }, 5);
            if (fields.Length < 5) continue;
            versions.Add(new FileVersion(fields[0].Trim(), fields[1], fields[2], ParseDate(fields[3]), fields[4].Trim()));
        }
        for (var i = 0; i < versions.Count; i++) versions[i].Version = versions.Count - i;
        return versions;
    }

    /// <summary>
    /// Parses <c>git log --name-only --format=HeaderFormat+%s</c> over the whole history into the
    /// newest version of every file, numbered by how many check-ins touched it.
    /// </summary>
    public static Dictionary<string, FileVersion> ParseLatestVersions(string output)
    {
        var latest = new Dictionary<string, FileVersion>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in output.Split(RecordSep))
        {
            var lines = record.Replace("\r", "").Split('\n');
            var fields = lines[0].Split(new[] { FieldSep }, 5);
            if (fields.Length < 5) continue;

            foreach (var line in lines.Skip(1))
            {
                var path = line.Trim();
                if (path.Length == 0) continue;
                if (!latest.ContainsKey(path))
                {
                    latest[path] = new FileVersion(fields[0], fields[1], fields[2], ParseDate(fields[3]), fields[4].Trim());
                    counts[path] = 0;
                }
                counts[path]++;
            }
        }

        foreach (var pair in latest) pair.Value.Version = counts[pair.Key];
        return latest;
    }

    private static DateTimeOffset ParseDate(string text) =>
        DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTimeOffset.MinValue;
}
