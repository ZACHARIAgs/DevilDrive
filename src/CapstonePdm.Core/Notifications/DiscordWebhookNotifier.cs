using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CapstonePdm.Core.Config;

namespace CapstonePdm.Core.Notifications;

public enum VaultEventKind
{
    CheckedOut,
    CheckedIn,
    UndoCheckOut,
    LockBroken,
}

public sealed class VaultEvent
{
    public VaultEvent(VaultEventKind kind, string user, IReadOnlyList<string> files)
    {
        Kind = kind;
        User = user;
        Files = files;
    }

    public VaultEventKind Kind { get; }
    public string User { get; }
    public IReadOnlyList<string> Files { get; }
    public string? Comment { get; set; }
    public string? PreviousOwner { get; set; }
    public IReadOnlyDictionary<string, int>? Versions { get; set; }
    public bool KeptCheckedOut { get; set; }
}

public interface INotifier
{
    Task NotifyAsync(VaultEvent e, CancellationToken ct = default);
}

/// <summary>Posts vault activity to a Discord channel webhook (URL comes from pdm.json).</summary>
public sealed class DiscordWebhookNotifier : INotifier
{
    private const int MaxFilesListed = 10;
    private const int DiscordLimit = 2000;

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Func<VaultConfig> _config;

    public DiscordWebhookNotifier(Func<VaultConfig> config)
    {
        _config = config;
    }

    public async Task NotifyAsync(VaultEvent e, CancellationToken ct = default)
    {
        var config = _config();
        var url = config.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url) || !config.Notify.IsEnabled(e.Kind)) return;

        var payload = new Dictionary<string, object>
        {
            ["username"] = config.VaultName,
            ["content"] = BuildMessage(e),
            // Comments are typed by users; never let them ping @everyone.
            ["allowed_mentions"] = new Dictionary<string, object> { ["parse"] = Array.Empty<string>() },
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Discord returned {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    internal static string BuildMessage(VaultEvent e)
    {
        var user = Escape(e.User);
        var files = FormatFiles(e.Files, e.Versions);
        string message;
        switch (e.Kind)
        {
            case VaultEventKind.CheckedOut:
                message = $"\U0001F512 **{user}** checked out {files}";
                break;
            case VaultEventKind.CheckedIn:
                message = $"✅ **{user}** checked in {files}";
                if (e.KeptCheckedOut) message += " (still checked out)";
                if (!string.IsNullOrWhiteSpace(e.Comment))
                {
                    message += "\n> " + Escape(e.Comment!.Trim()).Replace("\n", "\n> ");
                }
                break;
            case VaultEventKind.UndoCheckOut:
                message = $"↩️ **{user}** undid check-out of {files} (no changes saved)";
                break;
            case VaultEventKind.LockBroken:
                message = $"⚠️ **{user}** (admin) broke **{Escape(e.PreviousOwner ?? "someone")}**'s check-out of {files}";
                break;
            default:
                message = $"**{user}**: {files}";
                break;
        }
        return message.Length <= DiscordLimit ? message : message.Substring(0, DiscordLimit - 1) + "…";
    }

    private static string FormatFiles(IReadOnlyList<string> files, IReadOnlyDictionary<string, int>? versions)
    {
        var shown = files.Take(MaxFilesListed).Select(f =>
        {
            var text = "`" + f.Replace("`", "'") + "`";
            if (versions != null && versions.TryGetValue(f, out var v) && v > 0) text += $" (v{v})";
            return text;
        });
        var result = string.Join(", ", shown);
        if (files.Count > MaxFilesListed) result += $" and {files.Count - MaxFilesListed} more";
        return result;
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r", ""))
        {
            if (c == '\\' || c == '*' || c == '_' || c == '~' || c == '|' || c == '`') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
