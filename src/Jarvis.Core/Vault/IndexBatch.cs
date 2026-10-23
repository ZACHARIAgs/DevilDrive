using System;
using System.Collections.Generic;
using System.Text.Json;
using Jarvis.Core.Config;

namespace Jarvis.Core.Vault;

/// <summary>
/// Index records the SolidWorks add-in made for the desktop app: when SolidWorks files are checked in from
/// the app, it asks the add-in (inside SolidWorks) to read them, and gets this back as text over COM.
/// </summary>
public sealed class IndexBatch
{
    /// <summary>Vault-relative path to its record.</summary>
    public Dictionary<string, IndexEntry> Entries { get; set; } = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);

    /// <summary>3D previews that were saved (vault-relative, under .pdm/preview).</summary>
    public List<string> Previews { get; set; } = new List<string>();

    public string Serialize() => JsonSerializer.Serialize(this, Json.Options);

    public static IndexBatch? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var batch = JsonSerializer.Deserialize<IndexBatch>(json!, Json.Options);
            if (batch == null) return null;
            batch.Entries = new Dictionary<string, IndexEntry>(batch.Entries ?? new Dictionary<string, IndexEntry>(), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in batch.Entries.Values)
            {
                // Same clean-up as records read from the vault.
                entry.References ??= new List<string>();
                entry.ExternalReferences ??= new List<string>();
                entry.Properties = new Dictionary<string, string>(entry.Properties ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            }
            batch.Previews ??= new List<string>();
            return batch;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
