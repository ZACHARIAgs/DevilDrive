using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CapstonePdm.Core.Config;

namespace CapstonePdm.Core.Vault;

/// <summary>
/// What the SolidWorks add-in knows about a file: which SolidWorks saved it, what it references, and
/// its custom properties. Only SolidWorks can read these, so the add-in records them at check-in
/// (and with Rebuild Index), and the desktop app shows them.
/// </summary>
public sealed class IndexEntry
{
    /// <summary>SolidWorks version (year) that last saved the file.</summary>
    public int? SavedIn { get; set; }
    /// <summary>Files it directly references (vault-relative): an assembly's components, a drawing's models.</summary>
    public List<string> References { get; set; } = new List<string>();
    /// <summary>References outside the vault: teammates won't have these files.</summary>
    public List<string> ExternalReferences { get; set; } = new List<string>();
    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";

    /// <summary>First non-empty property among the given names (e.g. "PartNo", "Part Number").</summary>
    public string? GetProperty(params string[] names)
    {
        foreach (var name in names)
        {
            if (Properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    public static readonly string[] PartNumberNames = { PartNumberProperty, "PartNo", "Part Number", "Part No", "Number", "Drawing Number" };

    /// <summary>The custom property names the add-in writes. The other names above are still read, for older files.</summary>
    public const string PartNumberProperty = "PartNumber";
    public const string DescriptionProperty = "Description";
    public static readonly string[] DescriptionNames = { "Description", "Title" };
    public static readonly string[] MaterialNames = { "Material" };
    public static readonly string[] RevisionNames = { "Revision", "Rev" };
}

/// <summary>Where Jarvis keeps per-file extras in the vault.</summary>
public static class VaultPaths
{
    /// <summary>3D preview mesh (STL) the add-in saves for a part or assembly, e.g. .pdm/preview/Parts/bracket.SLDPRT.stl.</summary>
    public static string PreviewFor(string relativePath) => ".pdm/preview/" + relativePath.Replace('\\', '/') + ".stl";

    public static bool IsPreview(string relativePath) =>
        relativePath.Replace('\\', '/').StartsWith(".pdm/preview/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The index lives in the vault as one small file per CAD file (.pdm/index/&lt;path&gt;.json), so two
/// people checking in different files never conflict.
/// </summary>
internal static class VaultIndex
{
    public const string Folder = ".pdm/index/";

    public static string PathFor(string relativePath) => Folder + relativePath.Replace('\\', '/') + ".json";

    public static string Serialize(IndexEntry entry) => JsonSerializer.Serialize(entry, Json.Options) + "\n";

    public static IndexEntry? Parse(string json)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<IndexEntry>(json, Json.Options);
            if (entry == null) return null;
            entry.References ??= new List<string>();
            entry.ExternalReferences ??= new List<string>();
            entry.Properties = new Dictionary<string, string>(entry.Properties ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            return entry;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads every index entry on this PC (as of the last Get Latest).</summary>
    public static Dictionary<string, IndexEntry> ReadAll(string vaultRoot)
    {
        var result = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        var folder = Path.Combine(vaultRoot, ".pdm", "index");
        if (!Directory.Exists(folder)) return result;

        foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            var relative = file.Substring(folder.Length + 1).Replace('\\', '/');
            relative = relative.Substring(0, relative.Length - ".json".Length);
            try
            {
                var entry = Parse(File.ReadAllText(file));
                if (entry != null) result[relative] = entry;
            }
            catch (IOException)
            {
                // Skip unreadable entries; they'll be rewritten on the next check-in.
            }
        }
        return result;
    }

    /// <summary>Reverse of References: for each file, the files that use it.</summary>
    public static Dictionary<string, List<string>> WhereUsed(Dictionary<string, IndexEntry> index)
    {
        var usedIn = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in index)
        {
            foreach (var reference in pair.Value.References.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!usedIn.TryGetValue(reference, out var parents)) usedIn[reference] = parents = new List<string>();
                parents.Add(pair.Key);
            }
        }
        return usedIn;
    }
}
