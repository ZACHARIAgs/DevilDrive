using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Notifications;

namespace CapstonePdm.Core.Config;

/// <summary>
/// Team-wide settings stored as pdm.json in the vault root, so everyone gets the same values
/// through Get Latest. Admins edit it and check it in like any other file.
/// </summary>
public sealed class VaultConfig
{
    public const string FileName = "pdm.json";

    public string VaultName { get; set; } = "Capstone Vault";
    public string Remote { get; set; } = "origin";
    public string Branch { get; set; } = "main";
    public string? DiscordWebhookUrl { get; set; }

    /// <summary>GitHub usernames allowed to break other people's check-outs.</summary>
    public List<string> Admins { get; set; } = new List<string>();

    /// <summary>GitHub username to display name, for the vault view and Discord.</summary>
    public Dictionary<string, string> Team { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public NotifySettings Notify { get; set; } = new NotifySettings();

    public bool IsAdmin(string? gitHubLogin) =>
        !string.IsNullOrWhiteSpace(gitHubLogin) && Admins.Any(a => string.Equals(a, gitHubLogin, StringComparison.OrdinalIgnoreCase));

    public string DisplayNameFor(string gitHubLogin) =>
        Team.TryGetValue(gitHubLogin, out var name) && !string.IsNullOrWhiteSpace(name) ? name : gitHubLogin;

    public static VaultConfig Load(string vaultRoot)
    {
        var file = Path.Combine(vaultRoot, FileName);
        if (!File.Exists(file)) return new VaultConfig();

        try
        {
            var config = JsonSerializer.Deserialize<VaultConfig>(File.ReadAllText(file), Json.Options) ?? new VaultConfig();
            config.Normalize();
            return config;
        }
        catch (JsonException ex)
        {
            throw new PdmException($"{FileName} in the vault isn't valid JSON ({ex.Message}). Ask an admin to fix it.", ex.ToString());
        }
    }

    private void Normalize()
    {
        Team = new Dictionary<string, string>(Team ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        Admins ??= new List<string>();
        Notify ??= new NotifySettings();
        if (string.IsNullOrWhiteSpace(Remote)) Remote = "origin";
        if (string.IsNullOrWhiteSpace(Branch)) Branch = "main";
        if (string.IsNullOrWhiteSpace(VaultName)) VaultName = "Capstone Vault";
    }
}

public sealed class NotifySettings
{
    public bool CheckOut { get; set; } = true;
    public bool CheckIn { get; set; } = true;
    public bool UndoCheckOut { get; set; } = true;
    public bool BreakLock { get; set; } = true;
    public bool Freeze { get; set; } = true;

    public bool IsEnabled(VaultEventKind kind) => kind switch
    {
        VaultEventKind.CheckedOut => CheckOut,
        VaultEventKind.CheckedIn => CheckIn,
        VaultEventKind.UndoCheckOut => UndoCheckOut,
        VaultEventKind.LockBroken => BreakLock,
        VaultEventKind.Frozen or VaultEventKind.Unfrozen => Freeze,
        _ => true,
    };
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };
}
