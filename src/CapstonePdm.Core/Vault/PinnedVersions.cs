using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CapstonePdm.Core.Config;

namespace CapstonePdm.Core.Vault;

/// <summary>A file this PC uses an older version of (e.g. to try an assembly with last week's part).</summary>
public sealed class PinnedVersion
{
    public string Path { get; set; } = "";
    public string Commit { get; set; } = "";
    /// <summary>The version number shown to the user (1 = first check-in).</summary>
    public int Version { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>
/// Older versions used on this PC only, stored in .git/capstone-pinned.json (never committed, so
/// teammates and the vault keep the latest). Get Latest keeps them; "Return to latest" removes them.
/// </summary>
internal sealed class PinnedVersions
{
    public const string FileName = "capstone-pinned.json";

    public List<PinnedVersion> Pins { get; set; } = new List<PinnedVersion>();

    public PinnedVersion? Find(string relativePath) =>
        Pins.FirstOrDefault(p => string.Equals(p.Path, relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    public void Set(string relativePath, string commit, int version)
    {
        Remove(relativePath);
        Pins.Add(new PinnedVersion { Path = relativePath.Replace('\\', '/'), Commit = commit, Version = version, At = DateTimeOffset.Now });
    }

    public bool Remove(string relativePath) =>
        Pins.RemoveAll(p => string.Equals(p.Path, relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) > 0;

    public static string PathIn(string vaultRoot) => System.IO.Path.Combine(vaultRoot, ".git", FileName);

    public static PinnedVersions Load(string vaultRoot)
    {
        try
        {
            var file = PathIn(vaultRoot);
            if (!File.Exists(file)) return new PinnedVersions();
            var pins = JsonSerializer.Deserialize<PinnedVersions>(File.ReadAllText(file), Json.Options) ?? new PinnedVersions();
            pins.Pins ??= new List<PinnedVersion>();
            return pins;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return new PinnedVersions();
        }
    }

    public void Save(string vaultRoot)
    {
        var file = PathIn(vaultRoot);
        if (Pins.Count == 0)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        File.WriteAllText(file, JsonSerializer.Serialize(this, Json.Options));
    }
}
