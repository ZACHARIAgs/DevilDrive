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

    private void InitializeExportCommands()
    {
        ExportCommand = new RelayCommand(async () => await ExportAsync(),
            () => _vault != null && !IsBusy && _selected.Count > 0 && _selected.All(r => r.File.ExistsLocally || r.File.NotDownloaded));
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
            var temp = options.Zip ? Path.Combine(Path.GetTempPath(), "Jarvis export " + Guid.NewGuid().ToString("N").Substring(0, 8)) : null;
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
