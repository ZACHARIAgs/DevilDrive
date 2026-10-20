using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jarvis.Core.Config;

namespace Jarvis.Core.Vault;

/// <summary>An admin's request that whoever has a file checked out checks in their last saved version.</summary>
public sealed class AdminCheckInRequest
{
    public string Path { get; set; } = "";
    /// <summary>GitHub username holding the check-out when the request was made.</summary>
    public string Owner { get; set; } = "";
    /// <summary>GitHub username of the admin who asked.</summary>
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// Pending check-in requests, stored in the vault as .pdm/checkin-requests.json. The file owner's
/// own Jarvis (app or SolidWorks add-in) carries them out, because the saved file only
/// exists on that PC.
/// </summary>
internal sealed class CheckInRequestList
{
    public const string RelativePath = ".pdm/checkin-requests.json";

    public List<AdminCheckInRequest> Requests { get; set; } = new List<AdminCheckInRequest>();

    public AdminCheckInRequest? Find(string relativePath) =>
        Requests.FirstOrDefault(r => string.Equals(r.Path.Replace('\\', '/'), relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    public static CheckInRequestList Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new CheckInRequestList();
        try
        {
            var list = JsonSerializer.Deserialize<CheckInRequestList>(json!, Json.Options) ?? new CheckInRequestList();
            list.Requests ??= new List<AdminCheckInRequest>();
            return list;
        }
        catch (JsonException)
        {
            return new CheckInRequestList();
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json.Options) + "\n";
}
