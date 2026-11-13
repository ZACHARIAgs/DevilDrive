using System;
using System.Collections.Generic;
using System.IO;

namespace Jarvis.Core.Vault;

/// <summary>How safe it is to open a vault file with whatever Windows opens it with.</summary>
public enum OpenRisk
{
    /// <summary>CAD, documents and pictures: just open it.</summary>
    Safe,
    /// <summary>A type Jarvis doesn't know (or one that can carry macros): ask first.</summary>
    Ask,
    /// <summary>A program, script or shortcut: opening it would run it. Never opened from Jarvis.</summary>
    Blocked,
}

/// <summary>
/// Anyone on the team can check any file into the vault, and files that arrive through Git carry no
/// "downloaded from the internet" mark, so Windows would run a program or script without a warning.
/// Open (and double-click) therefore only opens file types that are data.
/// </summary>
public static class FileSafety
{
    private static readonly HashSet<string> Programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".msi", ".msp", ".msix", ".appx", ".appref-ms", ".application", ".gadget",
        ".ps1", ".psm1", ".psd1", ".ps1xml", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".jar", ".py", ".pyw",
        ".lnk", ".url", ".scf", ".inf", ".reg", ".cpl", ".dll", ".ocx", ".sys", ".msc", ".chm", ".hlp", ".library-ms", ".search-ms",
        ".settingcontent-ms", ".iso", ".img", ".vhd", ".vhdx",
    };

    private static readonly HashSet<string> Data = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".sldprt", ".sldasm", ".slddrw", ".step", ".stp", ".iges", ".igs", ".x_t", ".x_b", ".sat", ".stl", ".3mf", ".obj", ".dxf", ".dwg",
        ".easm", ".eprt", ".edrw", ".pdf", ".txt", ".md", ".csv", ".json", ".xml", ".log",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".svg", ".webp",
        ".docx", ".xlsx", ".pptx", ".zip",
    };

    public static OpenRisk RiskOf(string path)
    {
        // Windows ignores trailing dots and spaces ("part.exe." runs as part.exe).
        var extension = Path.GetExtension(path.TrimEnd('.', ' '));
        if (Programs.Contains(extension)) return OpenRisk.Blocked;
        return Data.Contains(extension) ? OpenRisk.Safe : OpenRisk.Ask;
    }
}
