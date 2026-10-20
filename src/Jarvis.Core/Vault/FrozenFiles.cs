using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jarvis.Core.Config;

namespace Jarvis.Core.Vault;

/// <summary>A file an admin froze, e.g. because it's released for production.</summary>
public sealed class FrozenEntry
{
    public string Path { get; set; } = "";
    /// <summary>GitHub username of the admin who froze it.</summary>
    public string By { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Revision letter given at this freeze (release), e.g. "B".</summary>
    public string Revision { get; set; } = "";
}

public static class Revisions
{
    // Letters drawing standards avoid because they're easily misread (I, O, Q, S, X, Z).
    private const string Letters = "ABCDEFGHJKLMNPRTUVWY";

    /// <summary>"" or null gives "A"; "A" gives "B"; "Y" gives "AA".</summary>
    public static string Next(string? current)
    {
        if (string.IsNullOrWhiteSpace(current)) return "A";
        var digits = current!.Trim().ToUpperInvariant().Select(c => Letters.IndexOf(c)).ToList();
        if (digits.Any(d => d < 0)) return "A";

        for (var i = digits.Count - 1; i >= 0; i--)
        {
            if (digits[i] < Letters.Length - 1)
            {
                digits[i]++;
                return new string(digits.Select(d => Letters[d]).ToArray());
            }
            digits[i] = 0;
        }
        return "A" + new string(digits.Select(d => Letters[d]).ToArray());
    }
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

    /// <summary>Last released revision of every file ever frozen (kept after unfreezing, so the next freeze continues the sequence).</summary>
    public Dictionary<string, string> Revisions { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? RevisionOf(string relativePath) =>
        Revisions.TryGetValue(relativePath.Replace('\\', '/'), out var revision) ? revision : null;

    public FrozenEntry? Find(string relativePath) =>
        Files.FirstOrDefault(f => string.Equals(f.Path.Replace('\\', '/'), relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    public static FrozenList Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FrozenList();
        try
        {
            var list = JsonSerializer.Deserialize<FrozenList>(json!, Json.Options) ?? new FrozenList();
            list.Files ??= new List<FrozenEntry>();
            list.Revisions = new Dictionary<string, string>(list.Revisions ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            return list;
        }
        catch (JsonException)
        {
            return new FrozenList();
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json.Options) + "\n";
}
