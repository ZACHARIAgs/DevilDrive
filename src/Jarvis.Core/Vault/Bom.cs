using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jarvis.Core.Vault;

/// <summary>One row of a bill of materials: a part (in one configuration) and how many the assembly uses.</summary>
public sealed class BomLine
{
    public string Path { get; set; } = "";
    public string Configuration { get; set; } = "";
    public int Quantity { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Latest released revision from the vault, if any.</summary>
    public string? Revision { get; set; }

    public string? GetProperty(params string[] names) =>
        names.Select(n => Properties.TryGetValue(n, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>Writes a flattened, parts-only bill of materials as CSV (opens in Excel).</summary>
public static class Bom
{
    public static string ToCsv(IEnumerable<BomLine> lines)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Item,Part Number,Description,Material,Qty,Revision,Configuration,File");
        var item = 1;
        foreach (var line in lines
                     .OrderBy(l => PartNumber(l), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(l => l.Configuration, StringComparer.OrdinalIgnoreCase))
        {
            var cells = new[]
            {
                (item++).ToString(),
                PartNumber(line),
                line.GetProperty(IndexEntry.DescriptionNames) ?? "",
                line.GetProperty(IndexEntry.MaterialNames) ?? "",
                line.Quantity.ToString(),
                line.Revision ?? line.GetProperty(IndexEntry.RevisionNames) ?? "",
                line.Configuration,
                System.IO.Path.GetFileName(line.Path),
            };
            csv.AppendLine(string.Join(",", cells.Select(Quote)));
        }
        return csv.ToString();
    }

    private static string PartNumber(BomLine line) =>
        line.GetProperty(IndexEntry.PartNumberNames) ?? System.IO.Path.GetFileNameWithoutExtension(line.Path);

    private static string Quote(string cell)
    {
        // A leading = + - @ would make Excel run the cell as a formula.
        if (cell.Length > 0 && "=+-@".IndexOf(cell[0]) >= 0) cell = "'" + cell;
        return cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
    }
}
