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
    public ICommand RenameCommand { get; private set; } = null!;
    public ICommand AddMcMasterCommand { get; private set; } = null!;
    public ICommand AddPartCommand { get; private set; } = null!;

    /// <summary>The folder the list shows ("" = the top of the vault): its own files, or everything below it while searching or filtering.</summary>
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
        RenameCommand = new RelayCommand(async () => await RenameAsync(), () => CanEdit && MoveTargets().Count == 1 && _selected.Count == 1);
        AddMcMasterCommand = new RelayCommand(async () => await AddMcMasterAsync(), () => CanEdit);
        AddPartCommand = new RelayCommand(async () => await AddPartAsync(), () => CanEdit);
    }

    /// <summary>
    /// Like File Explorer, a folder shows only its own files. While searching or filtering (e.g. "Checked out to me"),
    /// its subfolders are included too, so nothing is hidden by where it's filed.
    /// </summary>
    private bool InSelectedFolder(FileRowViewModel row)
    {
        if (SearchText.Length > 0 || FilterIndex != 0)
        {
            return SelectedFolder.Length == 0 || row.Path.StartsWith(SelectedFolder + "/", StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(row.File.Folder, SelectedFolder, StringComparison.OrdinalIgnoreCase);
    }

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
    private async Task AddPartAsync()
    {
        if (_vault == null) return;
        const string filter = "SolidWorks Files (*.sldprt;*.sldasm;*.slddrw)|*.sldprt;*.sldasm;*.slddrw|All Files (*.*)|*.*";
        var files = _dialogs.AskOpenFiles("Add Parts to Vault", filter);
        if (files == null || files.Count == 0) return;
        await AddFilesToFolderAsync(files, SelectedFolder);
    }

    /// <summary>
    /// Copies external files into the specified vault folder, unblocks them, offers part numbering if configured,
    /// and offers to check them in.
    /// </summary>
    public async Task AddFilesToFolderAsync(IReadOnlyList<string> sourceFiles, string? targetFolder)
    {
        if (_vault == null || sourceFiles == null || sourceFiles.Count == 0) return;

        // 1. Resolve destination folder. If target is empty/root, ask the user to pick a folder.
        var folder = string.IsNullOrWhiteSpace(targetFolder) ? null : targetFolder.Trim().Replace('/', '\\');
        if (string.IsNullOrEmpty(folder))
        {
            var folders = _vault.ListFolders(Files.Select(f => f.Path));
            folder = _dialogs.AskFolder("Select Destination Folder", _vault.Config.VaultName, folders, SelectedFolder,
                (parent, name) => _vault.CreateFolder(parent, name));
            if (string.IsNullOrEmpty(folder)) return;
        }

        var destinationDir = _vault.ToFullPath(folder);
        Directory.CreateDirectory(destinationDir);

        var numbering = _vault.LoadNumbering();
        var allExistingNames = Files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedRelatives = new List<string>();
        foreach (var source in sourceFiles)
        {
            if (!File.Exists(source)) continue;
            var fileName = Path.GetFileName(source);
            var ext = Path.GetExtension(source);

            // Part Numbering: if scheme is configured, offer to assign part number
            if (numbering != null && numbering.Groups.Count > 0 && IsCadFile(source))
            {
                var defaultGroup = numbering.Groups.FirstOrDefault();
                if (defaultGroup != null)
                {
                    var nextNum = numbering.NextNumber(defaultGroup, allExistingNames);
                    var formatted = numbering.Format(nextNum);
                    var suggestedName = $"{formatted}_{Path.GetFileNameWithoutExtension(source)}{ext}";
                    var chosen = _dialogs.AskText("Part Numbering",
                        $"Assign a part number for {fileName} (group: {defaultGroup.Name}):\nLeave unchanged to keep original name.",
                        suggestedName,
                        typed => VaultService.FileNameProblem(Path.Combine(folder, fileName), typed));
                    if (!string.IsNullOrWhiteSpace(chosen))
                    {
                        fileName = chosen.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? chosen : chosen + ext;
                    }
                }
            }

            var destPath = Path.Combine(destinationDir, fileName);
            var relativePath = Path.Combine(folder, fileName).Replace('\\', '/');

            if (File.Exists(destPath))
            {
                if (!_dialogs.Confirm("File Already Exists", $"{fileName} already exists in {folder}.\n\nDo you want to overwrite it?"))
                {
                    continue;
                }
            }

            try
            {
                File.Copy(source, destPath, overwrite: true);
                FileUtil.SetReadOnly(destPath, false);
                Jarvis.Core.Install.AppInstaller.RemoveDownloadMark(destPath);
                addedRelatives.Add(relativePath);
                allExistingNames.Add(fileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError(new PdmException($"Could not copy {fileName} to {folder}: {ex.Message}"));
            }
        }

        if (addedRelatives.Count == 0) return;

        SelectedFolder = folder;
        await RefreshAsync(quiet: true);

        var fileList = string.Join("\n", addedRelatives.Select(r => "• " + Path.GetFileName(r)));
        if (!_dialogs.Confirm("New Part", $"Added {(addedRelatives.Count == 1 ? Path.GetFileName(addedRelatives[0]) : $"{addedRelatives.Count} files")} to {folder}:\n\n{fileList}\n\nCheck in now so the whole team has it?"))
        {
            StatusText = $"Added {addedRelatives.Count} file(s) to {folder}. Check in when you're ready.";
            return;
        }

        var footnote = _vault.Config.SolidWorksVersion != null
            ? $"Only check in files saved in SolidWorks {_vault.Config.SolidWorksVersion}."
            : null;
        var request = _dialogs.AskCheckIn("Check In", "Check In", addedRelatives, "Added new part(s)", allowKeepCheckedOut: true, footnote: footnote);
        if (request == null)
        {
            StatusText = $"Added {addedRelatives.Count} file(s) to {folder}. Check in when you're ready.";
            return;
        }

        await RunAsync("Checking in…", async (vault, progress) =>
        {
            var (index, previews, note) = await RecordSolidWorksFilesAsync(vault, addedRelatives, progress);
            var result = await vault.CheckInAsync(addedRelatives, request.Comment, request.KeepCheckedOut, progress, indexUpdates: index, previews: previews);
            return Report(result, "Checked in");
        });
    }

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

    /// <summary>Renames one file in its folder; assemblies and drawings that use it are updated, like Move to Folder.</summary>
    private async Task RenameAsync()
    {
        if (_vault == null || MoveTargets() is not { Count: 1 } targets) return;
        var path = targets[0].Path;
        var name = _dialogs.AskText("Rename", $"New name for {targets[0].Name} (it stays a {Path.GetExtension(path)} file):",
            Path.GetFileNameWithoutExtension(path), typed => VaultService.FileNameProblem(path, typed));
        if (name == null) return;
        await RunMovesAsync(new[] { new FileMove(path, VaultService.RenamedPath(path, name)) }, folder: null);
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
        await RunMovesAsync(moves, folder);
    }

    /// <summary>Plans, confirms and carries out moves; <paramref name="folder"/> is where they go, or null for a rename.</summary>
    private async Task RunMovesAsync(IReadOnlyList<FileMove> moves, string? folder)
    {
        var title = folder == null ? "Rename" : "Move to Folder";
        await RunAsync(folder == null ? "Getting ready to rename\u2026" : "Getting ready to move\u2026", async (vault, progress) =>
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
                    throw new PdmException(folder == null
                        ? "It can't be renamed right now:\n\n" + string.Join("\n", plan.Problems.Select(p => "\u2022 " + p)) + "\n\nNothing was renamed."
                        : "These can't be moved right now:\n\n" + string.Join("\n", plan.Problems.Select(p => "\u2022 " + p)) + "\n\nNothing was moved.");
                }

                var message = folder == null
                    ? $"Rename {plan.Moves[0].From} to {PathList.Name(plan.Moves[0].To)}?"
                    : $"Move {PathList.Format(plan.Moves.Select(m => m.From).ToList())} to {FolderLabel(folder)}?";
                var users = plan.Parents.Where(p => !plan.Moves.Any(m => string.Equals(m.From, p, StringComparison.OrdinalIgnoreCase))).ToList();
                message += users.Count > 0
                    ? $"\n\nThese {users.Count} file(s) use {(plan.Moves.Count == 1 ? "it" : "them")}. SolidWorks updates them, and they're checked in together with the move:\n" +
                      string.Join("\n", users.Select(u => "\u2022 " + u))
                    : $"\n\nNo other vault files use {(plan.Moves.Count == 1 ? "it" : "them")}. The change is checked in right away, so teammates get it at their next Get Latest.";
                if (!_dialogs.Confirm(title, message)) return folder == null ? "Rename cancelled." : "Move cancelled.";

                var result = await Task.Run(() => vault.MoveAsync(plan, solidWorks, VaultService.MoveComment(plan.Moves), progress));
                if (folder == null) return Report(result, "Renamed");
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
