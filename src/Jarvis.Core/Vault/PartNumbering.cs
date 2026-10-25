using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core.Config;

namespace Jarvis.Core.Vault;

/// <summary>A group of part numbers, e.g. 200 = Turret: its parts are 201, 202, ... up to the next group.</summary>
public sealed class NumberGroup
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public override string ToString() => Name.Length > 0 ? $"{Number} \u2013 {Name}" : Number.ToString();
}

/// <summary>
/// A vault's part-numbering convention, set by an admin and kept in the vault (.pdm/numbering.json), e.g.
/// prefix TLT, 3 digits, groups 200 Turret and 400 Bending fixture: parts are TLT-201, TLT-202, TLT-401, ...
/// and files are named TLT-201_Turret_Plate.SLDPRT.
/// </summary>
public sealed class NumberingScheme
{
    public const string RelativePath = ".pdm/numbering.json";

    public string Prefix { get; set; } = "";
    /// <summary>Between the prefix and the number: TLT-201.</summary>
    public string Separator { get; set; } = "-";
    /// <summary>Digits in the number (3: 201; 4: 2001).</summary>
    public int Digits { get; set; } = 3;
    /// <summary>Between the part number and the descriptive name in file names: TLT-201_Turret_Plate.</summary>
    public string NameSeparator { get; set; } = "_";
    public List<NumberGroup> Groups { get; set; } = new List<NumberGroup>();

    public static NumberingScheme? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var scheme = JsonSerializer.Deserialize<NumberingScheme>(json!, Json.Options);
            if (scheme == null) return null;
            scheme.Groups ??= new List<NumberGroup>();
            scheme.Groups = scheme.Groups.OrderBy(g => g.Number).ToList();
            return scheme.Groups.Count > 0 ? scheme : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json.Options) + "\n";

    /// <summary>What's wrong with the scheme, or null. Shown by the admin's numbering window.</summary>
    public string? Problem()
    {
        if (Prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Separator.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            NameSeparator.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "The prefix and separators can't contain any of these: \\ / : * ? \" < > |";
        }
        if (Digits < 2 || Digits > 6) return "Use 2 to 6 digits.";
        if (Groups.Count == 0) return "Add at least one group, e.g. 100 Frame.";
        var max = (int)Math.Pow(10, Digits);
        foreach (var group in Groups)
        {
            if (group.Number <= 0 || group.Number >= max) return $"Group {group.Number} doesn't fit in {Digits} digits.";
            if (string.IsNullOrWhiteSpace(group.Name)) return $"Give group {group.Number} a name, e.g. Frame.";
        }
        if (Groups.Select(g => g.Number).Distinct().Count() != Groups.Count) return "Two groups have the same number.";
        return null;
    }

    /// <summary>"TLT-203" for 203.</summary>
    public string Format(int number) => Prefix + (Prefix.Length > 0 ? Separator : "") + number.ToString(new string('0', Digits));

    /// <summary>The numbers a group owns: from its number + 1 up to the next group (at most a hundred, or ten with 2 digits).</summary>
    public (int First, int Last) RangeOf(NumberGroup group)
    {
        var ordered = Groups.OrderBy(g => g.Number).ToList();
        var next = ordered.Where(g => g.Number > group.Number).Select(g => g.Number).DefaultIfEmpty((int)Math.Pow(10, Digits)).First();
        var span = Digits <= 2 ? 10 : 100;
        return (group.Number + 1, Math.Min(next, group.Number + span) - 1);
    }

    /// <summary>The number in a part number or file name that follows this scheme ("TLT-203_Plate.SLDPRT" is 203), or null.</summary>
    public int? NumberIn(string nameOrPartNumber)
    {
        var name = Path.GetFileNameWithoutExtension(nameOrPartNumber.Trim());
        var pattern = "^" + Regex.Escape(Prefix) + (Prefix.Length > 0 ? Regex.Escape(Separator) : "") + "(?<n>\\d{" + Digits + "})(?!\\d)";
        var match = Regex.Match(name, pattern, RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups["n"].Value) : null;
    }

    /// <summary>The group a number belongs to, or null.</summary>
    public NumberGroup? GroupOf(int number) => Groups.FirstOrDefault(g => number >= RangeOf(g).First && number <= RangeOf(g).Last);

    /// <summary>
    /// The next free number in a group, given the part numbers and file names already used (in the vault, and
    /// earlier in the same check-in). Throws when the group is full.
    /// </summary>
    public int NextNumber(NumberGroup group, IEnumerable<string> used)
    {
        var (first, last) = RangeOf(group);
        var taken = new HashSet<int>(used.Select(NumberInOrNull).Where(n => n != null).Select(n => n!.Value));
        for (var n = first; n <= last; n++)
        {
            if (!taken.Contains(n)) return n;
        }
        throw new Errors.PdmException($"Group {group} is full ({Format(first)} to {Format(last)} are all used). Ask an admin to add another group.");
    }

    private int? NumberInOrNull(string text) => NumberIn(text);

    /// <summary>"TLT-203_Bearing_Housing" from 203 and "Bearing housing": the file name (without extension) the convention asks for.</summary>
    public string FileName(int number, string descriptiveName)
    {
        var words = Regex.Split(descriptiveName ?? "", @"[^A-Za-z0-9\-]+").Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1));
        var name = string.Join("_", words);
        return name.Length == 0 ? Format(number) : Format(number) + NameSeparator + name;
    }

    /// <summary>A file name that follows the convention (prefix and a number in one of the groups).</summary>
    public bool Fits(string fileName) => NumberIn(fileName) is { } n && GroupOf(n) != null;

    /// <summary>An example for the admin window: "TLT-201_Turret_Plate.SLDPRT".</summary>
    public string Example()
    {
        var group = Groups.OrderBy(g => g.Number).FirstOrDefault() ?? new NumberGroup { Number = (int)Math.Pow(10, Digits - 1), Name = "Frame" };
        return FileName(group.Number + 1, group.Name + " plate") + ".SLDPRT";
    }
}
