using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jarvis.Core.Config;

/// <summary>Per-PC settings in %AppData%\Jarvis\settings.json.</summary>
public sealed class UserSettings
{
    public const string DefaultVaultPath = @"C:\SolarDevilsVault";

    public bool DarkMode { get; set; } = true;

    /// <summary>The vault the desktop app shows (one of <see cref="Vaults"/>).</summary>
    public string VaultPath { get; set; } = DefaultVaultPath;

    /// <summary>Every vault folder on this PC (one per group). The add-in uses whichever one holds the open file.</summary>
    public List<string> Vaults { get; set; } = new List<string>();

    public static string FilePath => Path.Combine(
        AppFolders.Roaming, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var defaults = new UserSettings();
                defaults.Normalize(); // the default vault is in the list even before settings are first saved
                return defaults;
            }
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Json.Options) ?? new UserSettings();
            if (string.IsNullOrWhiteSpace(settings.VaultPath)) settings.VaultPath = DefaultVaultPath;
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            var fresh = new UserSettings();
            fresh.Normalize();
            return fresh;
        }
    }

    /// <summary>Clean folder paths, no duplicates, and the current vault always in the list.</summary>
    internal void Normalize()
    {
        VaultPath = Clean(VaultPath);
        var list = (Vaults ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(Clean)
            .Select(v => string.Equals(v, VaultPath, StringComparison.OrdinalIgnoreCase) ? VaultPath : v).ToList();
        if (!list.Contains(VaultPath, StringComparer.OrdinalIgnoreCase)) list.Insert(0, VaultPath);
        Vaults = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void AddVault(string folder)
    {
        folder = Clean(folder);
        if (!Vaults.Contains(folder, StringComparer.OrdinalIgnoreCase)) Vaults.Add(folder);
    }

    /// <summary>Forgets a vault (the files stay on disk). The current vault moves to another one in the list.</summary>
    public void RemoveVault(string folder)
    {
        Vaults.RemoveAll(v => string.Equals(v, Clean(folder), StringComparison.OrdinalIgnoreCase));
        if (string.Equals(VaultPath, Clean(folder), StringComparison.OrdinalIgnoreCase)) VaultPath = Vaults.FirstOrDefault() ?? DefaultVaultPath;
        Normalize();
    }

    /// <summary>The vault folder that contains this file (the deepest one if vaults are nested), or null.</summary>
    public string? VaultFor(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
        return Vaults
            .Where(v => full.StartsWith(v + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(full, v, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Length)
            .FirstOrDefault();
    }

    private static string Clean(string folder)
    {
        try
        {
            return Path.GetFullPath(folder.Trim()).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return folder.Trim().TrimEnd('\\', '/');
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Options));
    }
}
