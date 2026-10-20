using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jarvis.Core.Vault;

/// <summary>How much Git LFS storage the vault uses: every version of every CAD file ever checked in.</summary>
public sealed class StorageUsage
{
    /// <summary>Total size of all stored versions (what GitHub counts against the free storage).</summary>
    public long AllVersionsBytes { get; set; }
    public int VersionCount { get; set; }
    /// <summary>Size of just the latest version of each file.</summary>
    public long LatestBytes { get; set; }
    public int FileCount { get; set; }
    public double LimitGB { get; set; }
    public double WarningPercent { get; set; }
    public DateTimeOffset MeasuredAt { get; set; }

    public double UsedGB => AllVersionsBytes / (1024.0 * 1024 * 1024);
    public double UsedPercent => LimitGB > 0 ? UsedGB / LimitGB * 100 : 0;
    public bool OverWarning => UsedPercent >= WarningPercent;

    public static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";
}

internal sealed class LfsFile
{
    public LfsFile(string name, long size, string oid, bool checkedOut)
    {
        Name = name;
        Size = size;
        Oid = oid;
        CheckedOut = checkedOut;
    }

    public string Name { get; }
    public long Size { get; }
    public string Oid { get; }
    /// <summary>The real file is on disk (false: only a small pointer, not downloaded).</summary>
    public bool CheckedOut { get; }

    /// <summary>Parses <c>git lfs ls-files --json</c>.</summary>
    public static List<LfsFile> Parse(string json)
    {
        var list = new List<LfsFile>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return list;
            foreach (var f in files.EnumerateArray())
            {
                list.Add(new LfsFile(
                    f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    f.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0,
                    f.TryGetProperty("oid", out var o) ? o.GetString() ?? "" : "",
                    !f.TryGetProperty("checkout", out var c) || c.ValueKind != JsonValueKind.False));
            }
        }
        catch (JsonException)
        {
            // Older git-lfs without --json: report nothing rather than fail.
        }
        return list;
    }
}

/// <summary>File types that are usually exports (re-creatable from the model), so checking them in is often a waste of storage.</summary>
public static class ExportFiles
{
    private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".dxf", ".dwg", ".step", ".stp", ".iges", ".igs", ".x_t", ".x_b", ".stl", ".3mf", ".obj",
    };

    public static bool IsExport(string path) => Extensions.Contains(System.IO.Path.GetExtension(path));
}
