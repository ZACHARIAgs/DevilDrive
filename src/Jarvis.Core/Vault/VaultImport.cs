using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jarvis.Core.Errors;

namespace Jarvis.Core.Vault;

/// <summary>One file being brought into the vault: where it is now and its new vault-relative path.</summary>
public sealed class ImportItem
{
    public ImportItem(string source, string target, bool useExisting = false, bool replaceExisting = false)
    {
        Source = source;
        Target = target;
        UseExisting = useExisting;
        ReplaceExisting = replaceExisting;
    }

    /// <summary>Full path of the original (left untouched).</summary>
    public string Source { get; }
    /// <summary>Vault-relative path of the copy, e.g. Turret/TLT-209_Clamp_Plate.SLDPRT.</summary>
    public string Target { get; }
    /// <summary>
    /// Not copied: the vault already has this file at <see cref="Target"/> (e.g. a part another Pack and Go also
    /// contains), and the imported assemblies and drawings use that one.
    /// </summary>
    public bool UseExisting { get; }
    /// <summary>
    /// The vault already has this file at <see cref="Target"/> and this one is a newer take on it (a teammate changed
    /// their copy): the vault's file is updated with it, as a new version. It must be checked out to the importer first.
    /// </summary>
    public bool ReplaceExisting { get; }
}

/// <summary>
/// Bringing someone's existing SolidWorks files into the vault (Import in the SolidWorks pane): copies them in under
/// new names (the vault's part-numbering convention, if it has one) and points every assembly and drawing at the
/// copies, so nothing still refers to the old folder. The originals are never changed.
/// </summary>
public static class VaultImport
{
    /// <summary>
    /// The name part of a file name, without a leading part number in the vault's convention:
    /// "TLT-203_Bearing_Housing.SLDPRT" gives "Bearing Housing"; "clamp plate v2.SLDPRT" gives "clamp plate v2".
    /// </summary>
    public static string DescriptiveName(string fileName, NumberingScheme? scheme)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (scheme?.NumberIn(name) is { } number)
        {
            var prefix = scheme.Format(number);
            name = name.Substring(Math.Min(prefix.Length, name.Length)).TrimStart('_', '-', ' ');
        }
        return name.Replace('_', ' ').Trim();
    }

    /// <summary>"Imported 3 file(s) and updated 2 from TLT_Main_Assembly".</summary>
    public static string CheckInComment(int added, int updated, string sourceName)
    {
        var parts = new List<string>();
        if (added > 0 || updated == 0) parts.Add($"Imported {added} file(s)");
        if (updated > 0) parts.Add((parts.Count == 0 ? "Updated " : "updated ") + $"{updated} file(s)");
        return string.Join(" and ", parts) + " from " + sourceName;
    }

    /// <summary>What stops the import (two files getting the same name, a name already in the vault), or nothing.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<ImportItem> items, Func<string, bool> existsInVault)
    {
        var problems = new List<string>();
        foreach (var item in items.Where(i => i.UseExisting && !existsInVault(i.Target)))
        {
            problems.Add($"{Path.GetFileName(item.Source)} should use the vault's {item.Target}, but it isn't in the vault.");
        }
        foreach (var item in items.Where(i => i.ReplaceExisting && !existsInVault(i.Target)))
        {
            problems.Add($"{Path.GetFileName(item.Source)} should update the vault's {item.Target}, but it isn't in the vault.");
        }
        foreach (var group in items.Where(i => i.ReplaceExisting).GroupBy(i => i.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"{string.Join(" and ", group.Select(i => Path.GetFileName(i.Source)))} would all update {group.Key}. Pick one.");
        }
        items = items.Where(i => !i.UseExisting && !i.ReplaceExisting).ToList();
        foreach (var group in items.GroupBy(i => i.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"{string.Join(" and ", group.Select(i => Path.GetFileName(i.Source)))} would all become {group.Key}. Give them different names.");
        }
        foreach (var item in items.Where(i => existsInVault(i.Target)))
        {
            problems.Add($"There's already a {item.Target} in the vault. Give {Path.GetFileName(item.Source)} another name.");
        }
        foreach (var item in items.Where(i => Path.GetFileName(i.Target).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileNameWithoutExtension(i.Target).Length == 0))
        {
            problems.Add($"\"{Path.GetFileName(item.Target)}\" isn't a valid file name.");
        }
        return problems;
    }

    /// <summary>
    /// Copies the files into the vault and repoints every copy's references from the originals to the other copies
    /// (or to the vault's own file, for items that use one).
    /// A reference matches an imported file by its full path or, failing that, by its file name when only one
    /// imported file has it: Pack and Go copies still store the paths on the PC that made them. References to
    /// files that aren't being imported are left as they are. An item that updates a vault file is copied over it
    /// (the caller has it checked out). If anything fails, the copies are removed again, updated files are put back
    /// as they were, and nothing is left behind. Returns the copies' full paths (new and updated). <paramref name="editor"/>
    /// may be null only when there are no SolidWorks files. Export uses this too, with the export folder as <paramref name="vaultRoot"/>.
    /// </summary>
    public static IReadOnlyList<string> CopyAndRepoint(string vaultRoot, IReadOnlyList<ImportItem> items, IReferenceEditor? editor, IProgress<string>? progress = null)
    {
        var root = Path.GetFullPath(vaultRoot).TrimEnd('\\');
        string Full(string relative) => Path.Combine(root, relative.Replace('/', '\\'));
        var newPlace = items.ToDictionary(i => Path.GetFullPath(i.Source), i => Full(i.Target), StringComparer.OrdinalIgnoreCase);
        var byName = items.GroupBy(i => Path.GetFileName(i.Source), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => Full(g.Single().Target), StringComparer.OrdinalIgnoreCase);
        string? CopyFor(string reference)
        {
            try
            {
                if (newPlace.TryGetValue(Path.GetFullPath(reference), out var copy)) return copy;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                // Not a usable path on this PC: go by its name.
            }
            return byName.TryGetValue(Path.GetFileName(reference), out var named) ? named : null;
        }
        var copied = new List<string>();
        // Vault files being updated: where each one's content from before is kept until the import has worked.
        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var toCopy = items.Where(i => !i.UseExisting).ToList();
            for (var i = 0; i < toCopy.Count; i++)
            {
                progress?.Report($"Copying {i + 1} of {toCopy.Count}: {Path.GetFileName(toCopy[i].Source)}\u2026");
                var target = Full(toCopy[i].Target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (toCopy[i].ReplaceExisting)
                {
                    var kept = Path.Combine(Path.GetTempPath(), "jarvis-import-" + Guid.NewGuid().ToString("N") + Path.GetExtension(target));
                    File.Copy(target, kept, overwrite: false);
                    before[target] = kept;
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                }
                File.Copy(toCopy[i].Source, target, overwrite: toCopy[i].ReplaceExisting);
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                copied.Add(target);
            }
            for (var i = 0; i < copied.Count; i++)
            {
                if (editor == null || !HasReferences(copied[i])) continue;
                progress?.Report($"Updating references ({i + 1} of {copied.Count}): {Path.GetFileName(copied[i])}\u2026");
                foreach (var reference in editor.GetReferences(copied[i]).ToList())
                {
                    var copy = CopyFor(reference);
                    if (copy == null || string.Equals(reference, copy, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!editor.ReplaceReference(copied[i], reference, copy))
                    {
                        throw new PdmException($"SolidWorks couldn't point {Path.GetFileName(copied[i])} at {Path.GetFileName(copy)}. Nothing was imported.");
                    }
                }
            }
            foreach (var kept in before.Values) TryDelete(kept);
            return copied;
        }
        catch
        {
            foreach (var pair in before)
            {
                try
                {
                    File.Copy(pair.Value, pair.Key, overwrite: true);
                    File.Delete(pair.Value);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Still checked out to the importer, so Undo Check Out puts the vault's version back.
                }
            }
            // Brand-new copies are removed; an updated file whose earlier content is back stays.
            foreach (var copy in copied.Where(c => !before.ContainsKey(c))) TryDelete(copy);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Left for the user to delete (in the vault it shows as new).
        }
    }

    /// <summary>Assemblies, drawings and parts can refer to other files (parts: derived or mirrored parts).</summary>
    private static bool HasReferences(string path) => VaultService.IsSolidWorksFile(path);
}
