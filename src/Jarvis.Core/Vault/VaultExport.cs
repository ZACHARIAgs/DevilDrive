using System;
using System.Collections.Generic;
using System.Linq;
using Jarvis.Core.Errors;

namespace Jarvis.Core.Vault;

/// <summary>What an export takes: the files, and references that can't come along.</summary>
public sealed class ExportSet
{
    /// <summary>Vault-relative paths: the chosen files, everything they use (all levels) and, if asked, their drawings.</summary>
    public List<string> Files { get; } = new List<string>();
    /// <summary>Drawings added because they show an exported file (not chosen themselves).</summary>
    public List<string> Drawings { get; } = new List<string>();
    /// <summary>"A uses B" for references outside the vault: the exported copy will look for B where it was.</summary>
    public List<string> Outside { get; } = new List<string>();
}

/// <summary>
/// Exporting files from the vault, like SolidWorks' Pack and Go: the chosen files plus everything they use,
/// copied to a folder (or zip) with every assembly and drawing pointed at the copies, so they open anywhere.
/// The copying and repointing is <see cref="VaultImport.CopyAndRepoint"/>, with the export folder as the root.
/// </summary>
public static class VaultExport
{
    /// <summary>
    /// The files to export. <paramref name="referencesOf"/> gives a file's direct references: vault-relative
    /// paths, and full paths for files outside the vault. <paramref name="drawingsOf"/> gives the drawings that
    /// show a file (from the vault index), used when <paramref name="includeDrawings"/> is set.
    /// </summary>
    public static ExportSet Collect(IEnumerable<string> selected, Func<string, IReadOnlyList<string>> referencesOf,
        Func<string, IEnumerable<string>> drawingsOf, bool includeDrawings)
    {
        var set = new ExportSet();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        void Add(string path)
        {
            if (!seen.Add(path)) return;
            set.Files.Add(path);
            queue.Enqueue(path);
        }

        var chosen = selected.ToList();
        foreach (var path in chosen) Add(path);
        while (true)
        {
            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                foreach (var reference in referencesOf(path))
                {
                    if (IsOutside(reference)) set.Outside.Add($"{PathList.Name(path)} uses {reference}");
                    else Add(reference);
                }
            }
            if (!includeDrawings) break;
            // Drawings of anything exported; a drawing can show other models too, which then come along.
            var drawings = set.Files.Where(f => !IsDrawing(f)).SelectMany(drawingsOf).Where(IsDrawing)
                .Where(d => !seen.Contains(d)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            if (drawings.Count == 0) break;
            foreach (var drawing in drawings)
            {
                set.Drawings.Add(drawing);
                Add(drawing);
            }
        }
        set.Outside.Sort(StringComparer.OrdinalIgnoreCase);
        return set;
    }

    /// <summary>
    /// Where each file goes in the export folder: its vault path (keeping the vault's folders) or just its
    /// name (everything in one folder, like Pack and Go's "flatten").
    /// </summary>
    public static IReadOnlyList<ImportItem> Items(string vaultRoot, IEnumerable<string> files, bool keepFolders) =>
        files.Select(f => new ImportItem(System.IO.Path.Combine(vaultRoot, f.Replace('/', '\\')), keepFolders ? f : PathList.Name(f))).ToList();

    /// <summary>A sensible name for the export: the first chosen file's name, plus "and N more" when there are several.</summary>
    public static string DefaultName(IReadOnlyList<string> selected) =>
        selected.Count == 0 ? "Jarvis export" :
        System.IO.Path.GetFileNameWithoutExtension(PathList.Name(selected[0])) + (selected.Count > 1 ? $" and {selected.Count - 1} more" : "");

    private static bool IsOutside(string reference) => reference.Contains(":") || reference.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool IsDrawing(string path) => path.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase);
}
