using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Jarvis.Client.Services;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;

namespace Jarvis.Client.ViewModels;

/// <summary>What the Export window asks: the folder or .zip to make, and what goes in it.</summary>
public sealed record ExportOptions(string Destination, bool Zip, bool KeepFolders, bool IncludeDrawings);

/// <summary>Export (like Pack and Go): selected files and everything they use, copied out with their references pointed at the copies.</summary>
public sealed partial class MainViewModel
{
    public ICommand ExportCommand { get; private set; } = null!;
    public ICommand ExportBomCommand { get; private set; } = null!;

    private void InitializeExportCommands()
    {
        ExportBomCommand = new RelayCommand(async () => await ExportBomAsync(),
            () => _vault != null && !IsBusy && SingleSelected is { } row && row.Path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) && (row.File.ExistsLocally || row.File.NotDownloaded));
        ExportCommand = new RelayCommand(async () => await ExportAsync(),
            () => _vault != null && !IsBusy && _selected.Count > 0 && _selected.All(r => r.File.ExistsLocally || r.File.NotDownloaded));
    }

    /// <summary>
    /// Export BOM (the same list as the SolidWorks pane's): the selected assembly's parts at every level with quantity,
    /// part number, description, material and released revision, as a CSV file that opens in Excel.
    /// SolidWorks reads the assembly in the background; the properties come from the vault's records.
    /// </summary>
    private async Task ExportBomAsync()
    {
        if (_vault == null || SingleSelected is not { } row) return;
        var rows = Files.ToDictionary(r => r.Path, r => r.File, StringComparer.OrdinalIgnoreCase);
        List<BomLine>? lines = null;
        await RunAsync("Reading the assembly\u2026", async (vault, progress) =>
        {
            // With "only download my folders", the assembly or its parts may be placeholders: get the real files first.
            IReadOnlyList<string> Recorded(string path) =>
                rows.TryGetValue(path, out var file) && file.Index != null ? file.Index.References : (IReadOnlyList<string>)Array.Empty<string>();
            var needed = VaultExport.Collect(new[] { row.Path }, Recorded, _ => Enumerable.Empty<string>(), includeDrawings: false);
            var missing = needed.Files.Where(f => rows.TryGetValue(f, out var file) && file.NotDownloaded).ToList();
            if (missing.Count > 0) await vault.DownloadAsync(missing, progress);

            SolidWorksAutomation? solidWorks = null;
            try
            {
                solidWorks = await Task.Run(() => SolidWorksAutomation.Connect(vault.Config.SolidWorksVersion, progress, "Exporting a BOM"));
                progress.Report("Reading the assembly's parts\u2026");
                var sw = solidWorks;
                var full = vault.ToFullPath(row.Path);
                lines = await Task.Run(() => sw.GetBom(full));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException)
            {
                throw new PdmException("SolidWorks couldn't read the assembly's parts. Open it in SolidWorks and use Export BOM in the DevilDrive pane instead.", ex.ToString());
            }
            finally
            {
                if (solidWorks != null) await Task.Run(solidWorks.Dispose);
            }

            foreach (var line in lines)
            {
                if (!line.Path.StartsWith(vault.VaultRoot + "\\", StringComparison.OrdinalIgnoreCase) || !rows.TryGetValue(vault.ToRelativePath(line.Path), out var file)) continue;
                if (file.Index != null) line.Properties = new Dictionary<string, string>(file.Index.Properties, StringComparer.OrdinalIgnoreCase);
                line.Revision = file.Revision;
            }
            return "Ready";
        });
        if (lines == null) return;
        if (lines.Count == 0)
        {
            _dialogs.ShowMessage("Export BOM", $"{row.Name} has no parts to list (suppressed and excluded-from-BOM parts are left out).", isWarning: false);
            return;
        }
        if (_dialogs.AskSaveFile("Export BOM", Path.GetFileNameWithoutExtension(row.Name) + " BOM.csv", "CSV for Excel (*.csv)|*.csv") is not { } destination) return;
        try
        {
            File.WriteAllText(destination, Bom.ToCsv(lines), new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowMessage("Export BOM", "Couldn't save the BOM: " + ex.Message, isWarning: true);
            return;
        }
        StatusText = $"Saved the BOM of {row.Name}: {lines.Count} part(s), {lines.Sum(l => l.Quantity)} in total.";
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{destination}\"") { UseShellExecute = true });
    }

    private async Task ExportAsync()
    {
        if (_vault == null || _selected.Count == 0) return;
        var chosen = _selected.Select(r => r.Path).ToList();
        if (_dialogs.AskExport(PathList.Format(chosen, 3), VaultExport.DefaultName(chosen)) is not { } options) return;
        var rows = Files.ToDictionary(r => r.Path, r => r.File, StringComparer.OrdinalIgnoreCase);

        await RunAsync("Getting ready to export…", async (vault, progress) =>
        {
            IEnumerable<string> Drawings(string path) => rows.TryGetValue(path, out var file) ? file.UsedIn : Enumerable.Empty<string>();
            IReadOnlyList<string> Recorded(string path) =>
                rows.TryGetValue(path, out var file) && file.Index != null ? file.Index.References.Concat(file.Index.ExternalReferences).ToList() : Array.Empty<string>();

            // From the vault index first, to download anything that's only a placeholder on this PC.
            var recorded = VaultExport.Collect(chosen, Recorded, Drawings, options.IncludeDrawings);
            var downloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            async Task DownloadMissingAsync(IEnumerable<string> files)
            {
                var missing = files.Where(f => !downloaded.Contains(f) && rows.TryGetValue(f, out var file) && file.NotDownloaded).ToList();
                if (missing.Count == 0) return;
                await vault.DownloadAsync(missing, progress);
                downloaded.UnionWith(missing);
            }
            await DownloadMissingAsync(recorded.Files);

            SolidWorksAutomation? solidWorks = null;
            var temp = options.Zip ? Path.Combine(Path.GetTempPath(), "DevilDrive export " + Guid.NewGuid().ToString("N").Substring(0, 8)) : null;
            try
            {
                if (recorded.Files.Any(VaultService.IsSolidWorksFile))
                {
                    solidWorks = await Task.Run(() => SolidWorksAutomation.Connect(vault.Config.SolidWorksVersion, progress, "Exporting SolidWorks files"));
                }
                // Then what the files really use, read by SolidWorks (the index can be missing or out of date).
                IReadOnlyList<string> Live(string path)
                {
                    if (solidWorks == null || !VaultService.IsSolidWorksFile(path)) return Recorded(path);
                    return solidWorks.GetReferences(vault.ToFullPath(path))
                        .Select(r => r.StartsWith(vault.VaultRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? vault.ToRelativePath(r) : r).ToList();
                }
                progress.Report("Finding everything the files use…");
                var set = await Task.Run(() => VaultExport.Collect(chosen, Live, Drawings, options.IncludeDrawings));
                await DownloadMissingAsync(set.Files);
                var gone = set.Files.Where(f => !File.Exists(vault.ToFullPath(f))).ToList();
                if (gone.Count > 0) throw new PdmException($"{PathList.Format(gone)} {(gone.Count == 1 ? "isn't" : "aren't")} on this PC. Get Latest, then export again.");

                var folder = temp == null ? options.Destination : Path.Combine(temp, Path.GetFileNameWithoutExtension(options.Destination));
                var items = VaultExport.Items(vault.VaultRoot, set.Files, options.KeepFolders);
                var problems = VaultImport.Problems(items, target => File.Exists(Path.Combine(folder, target.Replace('/', '\\'))));
                if (problems.Count > 0)
                {
                    throw new PdmException("The export can't be made like this:\n\n" + string.Join("\n", problems.Select(p => "• " + p)) +
                                           (options.KeepFolders ? "" : "\n\nTick \"Keep the vault's folders\" so files with the same name don't collide."));
                }

                var message = $"Export {set.Files.Count} file(s) to {options.Destination}?\n\n" + string.Join("\n", set.Files.Take(25).Select(f => "• " + f)) +
                              (set.Files.Count > 25 ? $"\n…and {set.Files.Count - 25} more" : "");
                if (set.Outside.Count > 0)
                {
                    message += "\n\nThese use files outside the vault, which aren't included (they'll be missing on other PCs):\n" +
                               string.Join("\n", set.Outside.Take(10).Select(o => "• " + o));
                }
                if (!_dialogs.Confirm("Export", message)) return "Export cancelled.";

                if (solidWorks != null) solidWorks.StoredPaths = true;
                await Task.Run(() => VaultImport.CopyAndRepoint(folder, items, solidWorks, progress));
                if (temp != null)
                {
                    progress.Report("Making the .zip file…");
                    await Task.Run(() => ZipFile.CreateFromDirectory(folder, options.Destination, CompressionLevel.Optimal, includeBaseDirectory: true));
                }
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{options.Destination}\"") { UseShellExecute = true });
                return $"Exported {set.Files.Count} file(s) to {options.Destination}.";
            }
            finally
            {
                if (solidWorks != null) await Task.Run(solidWorks.Dispose);
                if (temp != null)
                {
                    try
                    {
                        Directory.Delete(temp, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Only a temporary copy.
                    }
                }
            }
        });
    }
}
