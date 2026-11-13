using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Jarvis.Client.Services;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;

namespace Jarvis.Client.ViewModels;

/// <summary>
/// Import files (the same as the SolidWorks pane's): someone's SolidWorks files (a teammate's Pack and Go, say) copied
/// into the vault under the vault's names, with every assembly and drawing pointed at the copies, part numbers and
/// descriptions saved, and all checked in as one change. SolidWorks does the reading and rewriting in the background.
/// </summary>
public sealed partial class MainViewModel
{
    public ICommand ImportCommand { get; private set; } = null!;

    private void InitializeImportCommands()
    {
        ImportCommand = new RelayCommand(async () => await ImportAsync(), () => CanEdit);
    }

    private async Task ImportAsync()
    {
        if (_vault == null || _dialogs.AskFolderOnDisk("Pick the folder with the SolidWorks files to import (its subfolders are included)") is not { } picked) return;
        var source = Path.GetFullPath(picked).TrimEnd('\\');
        if ((source + "\\").StartsWith(_vault.VaultRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.ShowMessage("Import", "That folder is already in the vault. Open the files in SolidWorks and use Check In instead.", isWarning: false);
            return;
        }

        await RunAsync("Getting ready to import…", async (vault, progress) =>
        {
            SolidWorksAutomation? solidWorks = null;
            try
            {
                solidWorks = await Task.Run(() => SolidWorksAutomation.Connect(vault.Config.SolidWorksVersion, progress, "Importing SolidWorks files"));
                progress.Report("Looking for SolidWorks files…");
                var sw = solidWorks;
                var (files, missing) = await Task.Run(() => Scan(source, vault.VaultRoot, sw));
                if (files.Count == 0) throw new PdmException($"There are no SolidWorks files (parts, assemblies, drawings) in {source}.");

                // An open original would be used in place of its copy while the copies are repointed.
                var open = files.Where(f => FileUtil.IsInUse(f.Path)).Select(f => Path.GetFileName(f.Path)).ToList();
                if (open.Count > 0) throw new PdmException($"Close {PathList.Format(open)} in SolidWorks first, then import again.");
                if (missing.Count > 0 && !_dialogs.Confirm("Import",
                        "Some files use parts that aren't on this PC, so they'll be missing in the vault too:\n\n" + string.Join("\n", missing.Take(10).Select(m => "• " + m)) +
                        (missing.Count > 10 ? $"\n…and {missing.Count - 10} more." : "") + "\n\nImport anyway?"))
                {
                    return "Import cancelled.";
                }

                var vaultFiles = Files.Select(r => r.File).ToList();
                var inVault = new HashSet<string>(vaultFiles.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
                var plan = _dialogs.AskImport(files, source, vault.LoadNumbering(), VaultService.UsedPartNumbers(vaultFiles),
                    vault.ListFolders(vaultFiles.Select(f => f.RelativePath)), rel => inVault.Contains(rel) || File.Exists(vault.ToFullPath(rel)),
                    path => SameFileInVault(vault, path, vaultFiles));
                if (plan == null) return "Import cancelled.";

                // Vault files this import updates with someone's newer copy: checked out to you first, like any edit.
                var updates = plan.Where(p => p.Item.ReplaceExisting).Select(p => p.Item.Target).ToList();
                await vault.CheckOutForImportAsync(updates, progress);

                // Copying and repointing read the paths saved in the files: a Pack and Go names the PC it came from.
                solidWorks.StoredPaths = true;
                IReadOnlyList<string> copies;
                try
                {
                    copies = await Task.Run(() => VaultImport.CopyAndRepoint(vault.VaultRoot, plan.Select(p => p.Item).ToList(), sw, progress));
                }
                catch
                {
                    // The vault's files are back as they were: release them again.
                    if (updates.Count > 0) await vault.UndoCheckOutAsync(updates, progress);
                    throw;
                }
                solidWorks.StoredPaths = false;
                foreach (var (item, partNumber, description) in plan.Where(p => !p.Item.UseExisting && !p.Item.ReplaceExisting && VaultService.IsSolidWorksFile(p.Item.Target)))
                {
                    var properties = new Dictionary<string, string>();
                    if (partNumber != null) properties[IndexEntry.PartNumberProperty] = partNumber;
                    if (description.Length > 0) properties[IndexEntry.DescriptionProperty] = description;
                    if (properties.Count == 0) continue;
                    progress.Report($"Saving the part number and description of {PathList.Name(item.Target)}…");
                    var copy = vault.ToFullPath(item.Target);
                    await Task.Run(() => sw.SetProperties(copy, properties));
                }
                if (copies.Count == 0) return "Nothing to import: the vault already has every one of those files.";

                // Records come from the Jarvis add-in when it's running in this SolidWorks; otherwise from a later check-in.
                progress.Report("Making the vault records…");
                var batch = await Task.Run(() => sw.Record(vault.VaultRoot, copies));
                var index = batch?.Entries.ToDictionary(e => vault.ToFullPath(e.Key), e => (IndexEntry?)e.Value, StringComparer.OrdinalIgnoreCase);
                var result = await vault.CheckInAsync(copies, VaultImport.CheckInComment(copies.Count - updates.Count, updates.Count, Path.GetFileName(source)), progress: progress, indexUpdates: index);
                return Report(result, "Imported");
            }
            finally
            {
                if (solidWorks != null) await Task.Run(solidWorks.Dispose);
            }
        });
    }

    /// <summary>The SolidWorks files in the folder, plus files they use from other folders outside the vault; and references that aren't on this PC.</summary>
    private static (List<(string Path, bool FromElsewhere)> Files, List<string> Missing) Scan(string source, string vaultRoot, SolidWorksAutomation solidWorks)
    {
        bool InVault(string path) => path.StartsWith(vaultRoot + "\\", StringComparison.OrdinalIgnoreCase);
        var found = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(p => VaultService.IsSolidWorksFile(p) && !Path.GetFileName(p).StartsWith("~$", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var files = found.Select(p => (p, false)).ToList();
        var missing = new List<string>();
        var seen = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(found);
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            foreach (var reference in solidWorks.GetReferences(path))
            {
                if (InVault(reference) || !seen.Add(reference)) continue;
                if (!File.Exists(reference))
                {
                    missing.Add($"{Path.GetFileName(path)} uses {reference}");
                    continue;
                }
                files.Add((reference, true));
                queue.Enqueue(reference);
            }
        }
        return (files, missing);
    }

    /// <summary>The vault file with the same name as a file being imported (the one nearest the top), and whether it's the same file.</summary>
    private static (string RelativePath, bool Identical)? SameFileInVault(VaultService vault, string path, IReadOnlyList<VaultFile> vaultFiles)
    {
        var name = Path.GetFileName(path);
        var match = vaultFiles.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) && !f.IsVersionCopy)
            .OrderBy(f => f.RelativePath.Count(c => c == '/')).FirstOrDefault();
        if (match == null) return null;
        return (match.RelativePath, match.ExistsLocally && !match.NotDownloaded && FileUtil.SameContent(path, vault.ToFullPath(match.RelativePath)));
    }
}
