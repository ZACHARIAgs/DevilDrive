using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CapstonePdm.Core.Config;

namespace CapstonePdm.Core.Vault;

/// <summary>A file an admin froze, e.g. because it's released for production.</summary>
public sealed class FrozenEntry
{
    public string Path { get; set; } = "";
    /// <summary>GitHub username of the admin who froze it.</summary>
    public string By { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>
/// The list of frozen files, stored in the vault as .pdm/frozen.json so everyone sees it.
///
/// The list is for display (who, when, why). The enforcement is the Git LFS lock the freezing
/// admin keeps on each frozen file: nobody else can check it out, and pushes of changes to it
/// are refused.
/// </summary>
internal sealed class FrozenList
{
    public const string RelativePath = ".pdm/frozen.json";

    public List<FrozenEntry> Files { get; set; } = new List<FrozenEntry>();

    public FrozenEntry? Find(string relativePath) =>
        Files.FirstOrDefault(f => string.Equals(f.Path.Replace('\\', '/'), relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    public static FrozenList Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FrozenList();
        try
        {
            var list = JsonSerializer.Deserialize<FrozenList>(json!, Json.Options) ?? new FrozenList();
            list.Files ??= new List<FrozenEntry>();
            return list;
        }
        catch (JsonException)
        {
            return new FrozenList();
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json.Options) + "\n";
}
