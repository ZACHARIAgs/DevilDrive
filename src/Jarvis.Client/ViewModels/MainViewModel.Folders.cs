using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Jarvis.Client.Services;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;

namespace Jarvis.Client.ViewModels;

/// <summary>The folder tree: browsing by folder, making folders, and moving files between them.</summary>
public sealed partial class MainViewModel
{
    private string _selectedFolder = "";

    /// <summary>The tree's top level: one node, the vault itself.</summary>
    public ObservableCollection<FolderNodeViewModel> Folders { get; } = new();

    public ICommand NewFolderCommand { get; private set; } = null!;
    public ICommand DeleteFolderCommand { get; private set; } = null!;
    public ICommand ShowFolderInExplorerCommand { get; private set; } = null!;
    public ICommand MoveToFolderCommand { get; private set; } = null!;
    public ICommand AddMcMasterCommand { get; private set; } = null!;

    /// <summary>The folder the list shows ("" = the whole vault). Files in subfolders are included.</summary>
    public string SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (Set(ref _selectedFolder, value ?? "")) FilesView.Refresh();
        }
    }

    private void InitializeFolderCommands()
    {
        NewFolderCommand = new RelayCommand(NewFolder, () => CanEdit);
        DeleteFolderCommand = new RelayCommand(DeleteFolder, () => CanEdit && SelectedFolder.Length > 0);
        ShowFolderInExplorerCommand = new RelayCommand(ShowFolderInExplorer, () => _vault != null);
        MoveToFolderCommand = new RelayCommand(async () => await MoveToFolderAsync(), () => CanEdit && MoveTargets().Count > 0);
        AddMcMasterCommand = new RelayCommand(async () => await AddMcMasterAsync(), () => CanEdit);
    }

    private bool InSelectedFolder(FileRowViewModel row) =>
        SelectedFolder.Length == 0 || row.Path.StartsWith(SelectedFolder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rebuilds the tree from the files and the folders on disk, keeping what was open and selected.</summary>
    private void RebuildFolders()
    {
        if (_vault == null)
        {
            Folders.Clear();
            return;
        }
        var expanded = new HashSet<string>(AllNodes().Where(n => n.IsExpanded).Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
        var paths = _vault.ListFolders(Files.Select(f => f.Path));
        if (!paths.Contains(SelectedFolder, StringComparer.OrdinalIgnoreCase)) _selectedFolder = "";

        var root = new FolderNodeViewModel(_vault.Config.VaultName, "") { IsExpanded = true };
        var nodes = new Dictionary<string, FolderNodeViewModel>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        foreach (var path in paths)
        {
            var slash = path.LastIndexOf('/');
            var parent = slash < 0 ? "" : path.Substring(0, slash);
            var node = new FolderNodeViewModel(path.Substring(slash + 1), path) { IsExpanded = expanded.Contains(path) };
            nodes[path] = node;
            if (nodes.TryGetValue(parent, out var parentNode)) parentNode.Children.Add(node);
        }
        foreach (var node in nodes.Values)
        {
            node.FileCount = node.IsRoot ? Files.Count : Files.Count(f => f.Path.StartsWith(node.Path + "/", StringComparison.OrdinalIgnoreCase));
        }
        // Open the way down to the selected folder.
        for (var path = SelectedFolder; path.Length > 0; path = path.Contains('/') ? path.Substring(0, path.LastIndexOf('/')) : "")
        {
            if (nodes.TryGetValue(path, out var open) && path != SelectedFolder) open.IsExpanded = true;
        }
        if (nodes.TryGetValue(SelectedFolder, out var selected)) selected.IsSelected = true;

        Folders.Clear();
        Folders.Add(root);
    }

    private IEnumerable<FolderNodeViewModel> AllNodes()
    {
        var stack = new Stack<FolderNodeViewModel>(Folders);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    private string FolderLabel(string folder) => folder.Length == 0 ? $"the top of {_vault?.Config.VaultName ?? "the vault"}" : folder;

    private void NewFolder()
    {
        if (_vault == null) return;
        var parent = SelectedFolder;
        var name = _dialogs.AskText("New Folder", $"New folder in {FolderLabel(parent)}:", "New Folder", VaultService.FolderNameProblem);
        if (name == null) return;
        try
        {
            var created = _vault.CreateFolder(parent, name);
            _selectedFolder = created;
            RebuildFolders();
            FilesView.Refresh();
            OnPropertyChanged(nameof(SelectedFolder));
            StatusText = $"Made {created}. Drag files onto it (or right-click them, Move to Folder). Teammates see it once it has files in it.";
        }
        catch (PdmException ex)
        {
            _dialogs.ShowError(ex);
        }
    }

    private void DeleteFolder()
    {
        if (_vault == null || SelectedFolder.Length == 0) return;
        var folder = SelectedFolder;
        if (!_dialogs.Confirm("Delete Folder", $"Delete the empty folder {folder}?")) return;
        try
        {
            _vault.DeleteEmptyFolder(folder);
            var slash = folder.LastIndexOf('/');
            _selectedFolder = slash < 0 ? "" : folder.Substring(0, slash);
            RebuildFolders();
            FilesView.Refresh();
            OnPropertyChanged(nameof(SelectedFolder));
            StatusText = $"Deleted {folder}.";
        }
        catch (Exception ex) when (ex is PdmException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex as PdmException ?? new PdmException($"{folder} couldn't be deleted: {ex.Message}"));
        }
    }

    private void ShowFolderInExplorer()
    {
        if (_vault == null) return;
        var full = SelectedFolder.Length == 0 ? _vault.VaultRoot : _vault.ToFullPath(SelectedFolder);
        if (Directory.Exists(full)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// Adds a McMaster-Carr part: the dialog opens its page and waits for the download; the file then goes into
    /// COTS/MCMASTER (created if needed) and, if the user agrees, is checked in straight away.
    /// </summary>
    private async Task AddMcMasterAsync()
    {
        if (_vault == null || _dialogs.AskMcMasterPart() is not { } part) return;
        var relative = McMaster.VaultPathFor(part.DownloadedFile);
        var target = _vault.ToFullPath(relative);
        if (File.Exists(target))
        {
            _dialogs.ShowMessage("McMaster", $"{relative} is already in the vault, so the download was left in your Downloads folder.", isWarning: true);
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(part.DownloadedFile, target);
            Jarvis.Core.Install.AppInstaller.RemoveDownloadMark(target); // so SolidWorks doesn't treat it as blocked
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(new PdmException($"The download couldn't be moved into the vault: {ex.Message}"));
            return;
        }
        _selectedFolder = McMaster.Folder;
        OnPropertyChanged(nameof(SelectedFolder));
        await RefreshAsync(quiet: true);
        if (!_dialogs.Confirm("McMaster", $"Added {relative}.\n\nCheck it in now, so the whole team has it?"))
        {
            StatusText = $"Added {relative}. Check it in when you're ready.";
            return;
        }
        await RunAsync("Checking in\u2026", async (vault, progress) =>
            Report(await vault.CheckInAsync(new[] { relative }, $"Added McMaster-Carr part {part.PartNumber}", progress: progress), "Checked in"));
    }

    /// <summary>Files that can be picked up and moved: on this PC, in the vault or new.</summary>
    private List<FileRowViewModel> MoveTargets() =>
        _selected.Where(r => r.File.ExistsLocally && r.File.State is not (FileState.MissingLocally or FileState.CheckedOutByOther) &&
                             !VersionCopies.IsVersionCopy(r.Path)).ToList();

    private async Task MoveToFolderAsync()
    {
        if (_vault == null) return;
        var targets = MoveTargets();
        var folders = _vault.ListFolders(Files.Select(f => f.Path));
        var current = targets.Select(t => t.File.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? targets[0].File.Folder : null;
        var folder = _dialogs.AskFolder($"Move {(targets.Count == 1 ? targets[0].Name : $"{targets.Count} files")} to", _vault.Config.VaultName, folders, current,
            (parent, name) => _vault.CreateFolder(parent, name));
        if (folder == null) return;
        await MoveFilesAsync(targets.Select(t => t.Path).ToList(), folder);
    }

    /// <summary>
    /// Moves files into a folder (from the menu or by dragging onto the tree) and checks the move in. SolidWorks
    /// files need SolidWorks to update the assemblies and drawings that use them: it's started in the background.
    /// </summary>
    public async Task MoveFilesAsync(IReadOnlyList<string> paths, string folder)
    {
        if (_vault == null || paths.Count == 0) return;
        var moves = VaultService.IntoFolder(paths, folder).Where(m => !string.Equals(m.From, m.To, StringComparison.OrdinalIgnoreCase)).ToList();
        if (moves.Count == 0)
        {
            StatusText = $"Already in {FolderLabel(folder)}.";
            return;
        }

        await RunAsync("Getting ready to move\u2026", async (vault, progress) =>
        {
            SolidWorksAutomation? solidWorks = null;
            try
            {
                if (moves.Any(m => VaultService.IsSolidWorksFile(m.From)))
                {
                    solidWorks = await Task.Run(() => SolidWorksAutomation.Connect(vault.Config.SolidWorksVersion, progress));
                }
                var plan = await Task.Run(() => vault.PlanMoveAsync(moves, solidWorks, progress));
                if (!plan.CanMove)
                {
                    throw new PdmException("These can't be moved right now:\n\n" + string.Join("\n", plan.Problems.Select(p => "\u2022 " + p)) + "\n\nNothing was moved.");
                }

                var message = $"Move {PathList.Format(plan.Moves.Select(m => m.From).ToList())} to {FolderLabel(folder)}?";
                var users = plan.Parents.Where(p => !plan.Moves.Any(m => string.Equals(m.From, p, StringComparison.OrdinalIgnoreCase))).ToList();
                message += users.Count > 0
                    ? $"\n\nThese {users.Count} file(s) use {(plan.Moves.Count == 1 ? "it" : "them")}. SolidWorks updates them, and they're checked in together with the move:\n" +
                      string.Join("\n", users.Select(u => "\u2022 " + u))
                    : "\n\nThe move is checked in right away, so teammates get it at their next Get Latest.";
                if (!_dialogs.Confirm("Move to Folder", message)) return "Move cancelled.";

                var result = await Task.Run(() => vault.MoveAsync(plan, solidWorks, VaultService.MoveComment(plan.Moves), progress));
                _selectedFolder = folder;
                OnPropertyChanged(nameof(SelectedFolder));
                return Report(result, "Moved");
            }
            finally
            {
                if (solidWorks != null) await Task.Run(solidWorks.Dispose);
            }
        });
    }
}
