using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Jarvis.Client.Services;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;

namespace Jarvis.Client.ViewModels;

/// <summary>
/// Changing a file's description from the app. It's the Description property inside the SolidWorks file (BOMs and
/// drawings read it there), so SolidWorks writes it in the background, and the change is checked in like any other.
/// </summary>
public sealed partial class MainViewModel
{
    public ICommand EditDescriptionCommand { get; private set; } = null!;

    private void InitializeDescriptionCommands()
    {
        EditDescriptionCommand = new RelayCommand(async () => await EditDescriptionAsync(), () => CanEdit && DescriptionTarget() != null);
    }

    /// <summary>The one selected file whose description can be changed now: a SolidWorks file on this PC that nobody else has.</summary>
    private FileRowViewModel? DescriptionTarget() =>
        _selected.Count == 1 && _selected[0] is { } row && VaultService.IsSolidWorksFile(row.Path) && row.File.ExistsLocally && !row.File.NotDownloaded &&
        row.File.PinnedVersion == null && row.File.State is FileState.Available or FileState.CheckedOutByMe
            ? row
            : null;

    private async Task EditDescriptionAsync()
    {
        if (_vault == null || DescriptionTarget() is not { } row) return;
        var nameIsNumber = _vault.LoadNumbering()?.NameIsNumber == true;
        var text = _dialogs.AskText("Edit Description", $"Description of {row.Name}:", row.Description,
            typed => nameIsNumber && typed.Trim().Length == 0 ? "Type a description: the file name is only its part number, so this is what says what it is." : null);
        if (text == null || text == row.Description) return;

        var path = row.Path;
        var wasMine = row.File.State == FileState.CheckedOutByMe;
        await RunAsync("Changing the description…", async (vault, progress) =>
        {
            SolidWorksAutomation? solidWorks = null;
            try
            {
                solidWorks = await Task.Run(() => SolidWorksAutomation.Connect(vault.Config.SolidWorksVersion, progress, "Changing a description"));
                if (!wasMine)
                {
                    var checkedOut = await vault.CheckOutAsync(new[] { path }, progress);
                    if (checkedOut.Failures.Count > 0) return Report(checkedOut, "Checked out");
                }
                var full = vault.ToFullPath(path);
                try
                {
                    progress.Report($"Saving the description in {row.Name}…");
                    await Task.Run(() => solidWorks.SetProperties(full, new Dictionary<string, string> { [IndexEntry.DescriptionProperty] = text }));
                }
                catch
                {
                    if (!wasMine) await vault.UndoCheckOutAsync(new[] { path }, progress);
                    throw;
                }
                // Already checked out to you (maybe with other changes): it goes in with your next check-in.
                if (wasMine) return $"Saved the description in {row.Name}. It goes to the vault with your next check-in.";

                // The record shows the new description straight away (the add-in, if it's running, makes a full one).
                var record = (await Task.Run(() => solidWorks.Record(vault.VaultRoot, new[] { full })))?.Entries.Values.FirstOrDefault() ?? Patched(row.File.Index, text);
                var index = record == null ? null : new Dictionary<string, IndexEntry?>(StringComparer.OrdinalIgnoreCase) { [full] = record };
                return Report(await vault.CheckInAsync(new[] { path }, $"Changed the description of {row.Name}", progress: progress, indexUpdates: index), "Changed the description of");
            }
            finally
            {
                if (solidWorks != null) await Task.Run(solidWorks.Dispose);
            }
        });
    }

    /// <summary>A copy of the file's record with the new description, or null if it has none yet (it gets one at a later check-in).</summary>
    private static IndexEntry? Patched(IndexEntry? entry, string description)
    {
        if (entry == null) return null;
        return new IndexEntry
        {
            SavedIn = entry.SavedIn,
            References = entry.References.ToList(),
            ExternalReferences = entry.ExternalReferences.ToList(),
            Properties = new Dictionary<string, string>(entry.Properties, StringComparer.OrdinalIgnoreCase) { [IndexEntry.DescriptionProperty] = description },
            UpdatedAt = DateTimeOffset.Now,
            UpdatedBy = entry.UpdatedBy,
        };
    }
}
