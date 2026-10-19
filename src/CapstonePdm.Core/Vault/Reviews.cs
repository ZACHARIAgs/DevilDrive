using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CapstonePdm.Core.Config;

namespace CapstonePdm.Core.Vault;

/// <summary>A file submitted for review before release (an admin approves or rejects it).</summary>
public sealed class ReviewEntry
{
    public string Path { get; set; } = "";
    /// <summary>GitHub username of whoever submitted it.</summary>
    public string SubmittedBy { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// Files waiting for review, stored in the vault as .pdm/reviews.json. While a file is in review its
/// submitter keeps the file's lock, so nobody can change it; approving frees and freezes it (a release),
/// rejecting or withdrawing just frees it.
/// </summary>
internal sealed class ReviewList
{
    public const string RelativePath = ".pdm/reviews.json";

    public List<ReviewEntry> Reviews { get; set; } = new List<ReviewEntry>();

    public ReviewEntry? Find(string relativePath) =>
        Reviews.FirstOrDefault(r => string.Equals(r.Path.Replace('\\', '/'), relativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

    public static ReviewList Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ReviewList();
        try
        {
            var list = JsonSerializer.Deserialize<ReviewList>(json!, Json.Options) ?? new ReviewList();
            list.Reviews ??= new List<ReviewEntry>();
            return list;
        }
        catch (JsonException)
        {
            return new ReviewList();
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json.Options) + "\n";
}
