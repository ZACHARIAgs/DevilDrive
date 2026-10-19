using System;
using System.IO;
using System.Text.Json;

namespace CapstonePdm.Core.Config;

/// <summary>Per-PC settings in %AppData%\CapstonePDM\settings.json.</summary>
public sealed class UserSettings
{
    public const string DefaultVaultPath = @"C:\CapstoneVault";

    public string VaultPath { get; set; } = DefaultVaultPath;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CapstonePDM", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UserSettings();
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Json.Options) ?? new UserSettings();
            if (string.IsNullOrWhiteSpace(settings.VaultPath)) settings.VaultPath = DefaultVaultPath;
            return settings;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Options));
    }
}
