using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jarvis.Core.Config;

namespace Jarvis.Core.Vault;

/// <summary>A teammate asking the admins to delete a file from the vault, and why.</summary>
public sealed class DeleteRequest
{
    public string Path { get; set; } = "";
    /// <summary>GitHub username of whoever asked.</summary>
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>
/// Pending delete requests, stored in the vault as .pdm/delete-requests.json so every admin sees them.
/// Anyone may add their own or take it back; only admins delete the file or reject the request
/// (the vault guard checks that).
/// </summary>
internal sealed class DeleteRequestList
{
    public const string RelativePath = ".pdm/delete-requests.json";

    public List<DeleteRequest> Requests { get; set; } = new List<DeleteRequest>();

    public DeleteRequest? Find(string relativePath) =>
        Requests.FirstOrDefault(r => string.Equals(r.Path.Replace('\\', '/'), relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    public static DeleteRequestList Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DeleteRequestList();
        try
        {
            var list = JsonSerializer.Deserialize<DeleteRequestList>(json!, Json.Options) ?? new DeleteRequestList();
            list.Requests ??= new List<DeleteRequest>();
            return list;
        }
        catch (JsonException)
        {
            return new DeleteRequestList();
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json.Options) + "\n";
}
