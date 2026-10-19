using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CapstonePdm.Client.Services;
using CapstonePdm.Core.Config;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Vault;

namespace CapstonePdm.Client.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private static readonly Brush InfoBrush = Frozen(0xE3, 0xF2, 0xFD);
    private static readonly Brush WarningBrush = Frozen(0xFF, 0xF3, 0xE0);
    private static readonly Brush ErrorBrush = Frozen(0xFF, 0xEB, 0xEE);

    private readonly IDialogs _dialogs;
    private readonly Dictionary<string, FileRowViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromSeconds(60) };

    private VaultService? _vault;
    private bool _isBusy;
    private string _statusText = "Starting…";
    private string _bannerText = "";
    private Brush _bannerBrush = InfoBrush;
    private string _searchText = "";
    private int _filterIndex;
    private string _infoText = "";
    private IReadOnlyList<FileRowViewModel> _selected = Array.Empty<FileRowViewModel>();

    public MainViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = o => o is FileRowViewModel row && MatchesFilter(row);
        FilesView.SortDescriptions.Add(new SortDescription(nameof(FileRowViewModel.Path), ListSortDirection.Ascending));
        _autoRefresh.Tick += async (_, _) => await RefreshAsync(quiet: true);

        GetLatestCommand = new RelayCommand(async () => await GetLatestAsync(), () => CanEdit);
        CheckOutCommand = new RelayCommand(async () => await CheckOutAsync(), () => CanEdit && CheckOutTargets().Count > 0);
        CheckInCommand = new RelayCommand(async () => await CheckInAsync(), () => CanEdit && CheckInTargets().Count > 0);
        UndoCheckOutCommand = new RelayCommand(async () => await UndoCheckOutAsync(), () => CanEdit && UndoTargets().Count > 0);
        HistoryCommand = new RelayCommand(ShowHistory, () => _vault != null && SingleSelected?.File.State is not (null or FileState.NotInVault));
        RefreshCommand = new RelayCommand(async () => await RefreshAsync(quiet: false), () => CanRun);
        BreakLockCommand = new RelayCommand(async () => await BreakLockAsync(), () => CanEdit && IsAdmin && SingleSelected?.File.Lock != null && SingleSelected.File.State != FileState.Frozen);
        FreezeCommand = new RelayCommand(async () => await FreezeAsync(), () => CanEdit && IsAdmin && FreezeTargets().Count > 0);
        UnfreezeCommand = new RelayCommand(async () => await UnfreezeAsync(), () => CanEdit && IsAdmin && UnfreezeTargets().Count > 0);
        RequestCheckInCommand = new RelayCommand(async () => await RequestCheckInAsync(), () => CanEdit && IsAdmin && SingleSelected?.File.State == FileState.CheckedOutByOther);
        OpenCommand = new RelayCommand(OpenSelected, () => SingleSelected?.File.ExistsLocally == true);
        ShowInExplorerCommand = new RelayCommand(ShowInExplorer, () => _vault != null);
        SettingsCommand = new RelayCommand(async () => await OpenSettingsAsync(), () => !IsBusy);
        MilestonesCommand = new RelayCommand(() => _dialogs.ShowMilestones(), () => CanRun);
        ReturnToLatestCommand = new RelayCommand(async () => await ReturnToLatestAsync(), () => CanRun && IsViewingMilestone);
        Open3DCommand = new RelayCommand(Open3D, () => Details?.Model != null);
        DownloadCommand = new RelayCommand(async () => await DownloadAsync(), () => CanEdit && _selected.Any(r => r.File.NotDownloaded));
        UpdateVaultSetupCommand = new RelayCommand(async () => await UpdateVaultSetupAsync(), () => CanEdit && IsAdmin && HasSetupProblems);
    }

    public ObservableCollection<FileRowViewModel> Files { get; } = new();
    public ICollectionView FilesView { get; }

    public ICommand GetLatestCommand { get; }
    public ICommand CheckOutCommand { get; }
    public ICommand CheckInCommand { get; }
    public ICommand UndoCheckOutCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand BreakLockCommand { get; }
    public ICommand FreezeCommand { get; }
    public ICommand UnfreezeCommand { get; }
    public ICommand RequestCheckInCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand ShowInExplorerCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand MilestonesCommand { get; }
    public ICommand ReturnToLatestCommand { get; }
    public ICommand UpdateVaultSetupCommand { get; }
    public ICommand Open3DCommand { get; }
    public ICommand DownloadCommand { get; }

    private VaultSnapshot? _snapshot;

    private bool _hasSetupProblems;

    /// <summary>Admins only: the vault guard is missing/changed or storage rules are missing.</summary>
    public bool HasSetupProblems
    {
        get => _hasSetupProblems;
        private set => Set(ref _hasSetupProblems, value);
    }

    public IVaultService? Vault => _vault;

    public IReadOnlyList<FileRowViewModel> SelectedFiles
    {
        get => _selected;
        set
        {
            _selected = value;
            CommandManager.InvalidateRequerySuggested();
            UpdateDetails(reloadThumbnail: true);
        }
    }

    private DetailsViewModel? _details;

    /// <summary>The details panel's content; null when zero or several files are selected.</summary>
    public DetailsViewModel? Details
    {
        get => _details;
        private set => Set(ref _details, value);
    }

    private void UpdateDetails(bool reloadThumbnail)
    {
        var row = SingleSelected;
        if (row == null)
        {
            Details = null;
            return;
        }

        var previous = Details;
        var details = BuildDetails(row);
        var sameFile = previous != null && string.Equals(previous.Row.Path, row.Path, StringComparison.OrdinalIgnoreCase);
        if (sameFile && !reloadThumbnail) details.Thumbnail = previous!.Thumbnail;

        // 3D preview saved by the SolidWorks add-in (.pdm/preview/<path>.stl); shown by default when there is one.
        if (_vault != null)
        {
            var preview = _vault.ToFullPath(VaultPaths.PreviewFor(row.Path));
            if (System.IO.File.Exists(preview))
            {
                details.PreviewPath = preview;
                details.PreviewWritten = System.IO.File.GetLastWriteTime(preview);
                details.Show3D = !sameFile || previous!.Show3D;
                details.PreviewNote = $"Drag to rotate, right-drag to pan, scroll to zoom. Simplified 3D preview from {details.PreviewWritten:MMM d, h:mm tt}.";
                if (sameFile && previous!.Model != null && previous.PreviewWritten == details.PreviewWritten) details.Model = previous.Model;
            }
            else if (IsCadFile(row.Path) && !row.Path.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase))
            {
                details.PreviewNote = "No 3D preview yet. It's made when the file is checked in from SolidWorks, or with Rebuild Index in the SolidWorks pane.";
            }
        }

        Details = details;
        if (!sameFile || reloadThumbnail) _ = LoadThumbnailAsync(details);
        if (details.HasPreviewFile && details.Model == null) _ = Load3DAsync(details);
    }

    private async Task Load3DAsync(DetailsViewModel details)
    {
        details.Loading3D = true;
        try
        {
            var model = await StlLoader.LoadAsync(details.PreviewPath!);
            if (ReferenceEquals(Details, details)) details.Model = model;
        }
        catch (Exception)
        {
            details.Show3D = false;
        }
        finally
        {
            details.Loading3D = false;
        }
    }

    private Task DownloadAsync()
    {
        var targets = _selected.Where(r => r.File.NotDownloaded).Select(r => r.Path).ToList();
        return RunAsync("Downloading…", async (vault, progress) =>
        {
            await vault.DownloadAsync(targets, progress);
            return $"Downloaded {PathList.Format(targets)}.";
        });
    }

    /// <summary>Top-level vault folders, for choosing which to download.</summary>
    private List<string> VaultFolders() =>
        Files.Select(r => r.Path).Where(p => p.Contains('/')).Select(p => p.Substring(0, p.IndexOf('/')))
            .Where(f => !f.StartsWith(".", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    private void Open3D()
    {
        if (Details?.Model != null) _dialogs.Show3D(Details.Name, Details.Model);
    }

    private async Task LoadThumbnailAsync(DetailsViewModel details)
    {
        if (_vault == null || !details.Row.File.ExistsLocally) return;
        details.LoadingThumbnail = true;
        var image = await ShellThumbnail.GetAsync(_vault.ToFullPath(details.Row.Path), 256);
        details.LoadingThumbnail = false;
        if (ReferenceEquals(Details, details)) details.Thumbnail = image;
    }

    private DetailsViewModel BuildDetails(FileRowViewModel row)
    {
        var f = row.File;
        var facts = new List<PropertyRow>();
        if (f.Version > 0) facts.Add(new PropertyRow("Version", f.Version.ToString()));
        if (f.Revision != null) facts.Add(new PropertyRow("Revision", f.State == FileState.Frozen ? $"{f.Revision} (released)" : $"{f.Revision} (last released; changed since)"));
        if (f.LatestVersion != null)
        {
            facts.Add(new PropertyRow("Last check-in", $"{f.LatestVersion.Date.LocalDateTime:MMM d, yyyy h:mm tt} by {f.LatestVersion.Author}"));
            if (f.LatestVersion.CommentFirstLine.Length > 0) facts.Add(new PropertyRow("Comment", f.LatestVersion.CommentFirstLine));
        }
        if (f.Lock != null && f.State != FileState.Frozen)
        {
            facts.Add(new PropertyRow("Checked out", $"{f.CheckedOutBy}" + (f.Lock.LockedAt != null ? $", {f.Lock.LockedAt.Value.LocalDateTime:MMM d, h:mm tt}" : "")));
        }
        if (f.State == FileState.Frozen && f.Freeze != null)
        {
            facts.Add(new PropertyRow("Frozen", $"{f.FrozenBy}, {f.Freeze.At.LocalDateTime:MMM d, yyyy}"));
            if (f.Freeze.Reason.Length > 0) facts.Add(new PropertyRow("Reason", f.Freeze.Reason));
        }
        if (f.CheckInRequest != null)
        {
            facts.Add(new PropertyRow("Check-in requested", $"{f.CheckInRequestedBy}, {f.CheckInRequest.RequestedAt.LocalDateTime:MMM d, h:mm tt}"));
        }
        facts.Add(new PropertyRow("Folder", row.Folder));

        var index = f.Index;
        var team = _vault?.Config.SolidWorksVersion;
        var warnings = new List<string>();
        if (index?.SavedIn != null)
        {
            facts.Add(new PropertyRow("Saved in", $"SolidWorks {index.SavedIn}"));
            if (team != null && index.SavedIn > team) warnings.Add($"Saved in SolidWorks {index.SavedIn}, newer than the team's {team}: teammates on {team} can't open it.");
        }
        if (f.ChangedSinceDrawing.Count > 0)
        {
            warnings.Add($"This drawing may be out of date: {PathList.Format(f.ChangedSinceDrawing)} changed after it was last checked in.");
        }
        if (f.StaleDrawings.Count > 0)
        {
            warnings.Add($"{PathList.Format(f.StaleDrawings)} {(f.StaleDrawings.Count == 1 ? "hasn't" : "haven't")} been checked in since this file changed. Update the drawing?");
        }
        if (f.Drawings.Count > 0) facts.Add(new PropertyRow("Drawings", string.Join(", ", f.Drawings.Select(PathList.Name))));
        if (index != null && index.ExternalReferences.Count > 0)
        {
            warnings.Add("References files outside the vault, which teammates won't have:\n" + string.Join("\n", index.ExternalReferences));
        }

        return new DetailsViewModel(row)
        {
            Facts = facts,
            Warnings = warnings,
            Properties = index?.Properties.OrderBy(p => p.Key).Select(p => new PropertyRow(p.Key, p.Value)).ToList() ?? new List<PropertyRow>(),
            Contains = index?.References.OrderBy(r => r).ToList() ?? new List<string>(),
            WhereUsed = f.UsedIn.OrderBy(r => r).ToList(),
            IndexNote = index != null
                ? $"Properties and references as of {index.UpdatedAt.LocalDateTime:MMM d, h:mm tt} ({index.UpdatedBy}). They update when the file is checked in from SolidWorks."
                : IsCadFile(f.RelativePath)
                    ? "No properties or references recorded yet. They're filled in when the file is checked in from SolidWorks, or with Rebuild Index in the SolidWorks pane."
                    : "",
        };
    }

    private static bool IsCadFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".sldprt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".sldasm", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".slddrw", StringComparison.OrdinalIgnoreCase);
    }

    private FileRowViewModel? SingleSelected => _selected.Count == 1 ? _selected[0] : null;
    private bool CanRun => _vault != null && !IsBusy;

    /// <summary>Commands that change the vault are off while a (read-only) milestone is shown.</summary>
    private bool CanEdit => CanRun && !IsViewingMilestone;

    private Milestone? _viewingMilestone;

    public bool IsViewingMilestone => _viewingMilestone != null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string InfoText
    {
        get => _infoText;
        private set => Set(ref _infoText, value);
    }

    public string BannerText
    {
        get => _bannerText;
        private set
        {
            if (Set(ref _bannerText, value)) OnPropertyChanged(nameof(HasBanner));
        }
    }

    public bool HasBanner => BannerText.Length > 0;

    public Brush BannerBrush
    {
        get => _bannerBrush;
        private set => Set(ref _bannerBrush, value);
    }

    public bool IsAdmin => _vault?.IsAdmin == true;

    public string WindowTitle => _vault == null ? "Capstone PDM" : $"Capstone PDM — {_vault.Config.VaultName} ({_vault.VaultRoot})";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) FilesView.Refresh();
        }
    }

    /// <summary>0 all, 1 checked out to me, 2 checked out by others, 3 needs attention, 4 updates available.</summary>
    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (Set(ref _filterIndex, value)) FilesView.Refresh();
        }
    }

    // ------------------------------------------------------------ startup and refresh

    public async Task InitializeAsync()
    {
        await OpenVaultAsync(UserSettings.Load().VaultPath);
        _autoRefresh.Start();
    }

    private async Task OpenVaultAsync(string path)
    {
        try
        {
            StatusText = "Opening vault…";
            _vault = await VaultService.OpenAsync(path);
        }
        catch (PdmException ex)
        {
            _vault = null;
            Files.Clear();
            _rows.Clear();
            SetBanner(ex.Message + " Use Settings to choose the vault folder.", ErrorBrush);
            StatusText = "No vault open";
            OnPropertyChanged(nameof(WindowTitle));
            _dialogs.ShowError(ex);
            return;
        }

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsAdmin));
        await RefreshAsync(quiet: false);
    }

    public async Task RefreshAsync(bool quiet)
    {
        if (_vault == null || IsBusy) return;
        IsBusy = true;
        var previousStatus = StatusText;
        StatusText = "Refreshing…";
        try
        {
            var snapshot = await _vault.GetSnapshotAsync(contactVault: true);
            Apply(snapshot);
            StatusText = quiet ? previousStatus : "Ready";
            if (snapshot.CheckInRequestsForMe > 0) await CarryOutCheckInRequestsAsync();
        }
        catch (PdmException ex)
        {
            StatusText = "Refresh failed";
            if (quiet) SetBanner(ex.Message, ErrorBrush);
            else _dialogs.ShowError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>An admin asked this user to check files in: do it, then tell them what happened.</summary>
    private async Task CarryOutCheckInRequestsAsync()
    {
        StatusText = "Checking in files an admin asked for…";
        try
        {
            var result = await _vault!.ProcessCheckInRequestsAsync(new Progress<string>(s => StatusText = s));
            if (result.Succeeded.Count > 0 || result.Failures.Count > 0) StatusText = Report(result, "Checked in (admin request)");
            Apply(await _vault.GetSnapshotAsync(contactVault: false));
        }
        catch (PdmException ex)
        {
            StatusText = "Admin check-in request not done";
            SetBanner("An admin asked you to check in a file, but it couldn't be done automatically: " + ex.Message +
                      " Save and close it in SolidWorks; it will be tried again within a minute.", ErrorBrush);
        }
    }

    private void Apply(VaultSnapshot snapshot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
        {
            seen.Add(file.RelativePath);
            if (_rows.TryGetValue(file.RelativePath, out var row))
            {
                row.Update(file);
            }
            else
            {
                row = new FileRowViewModel(file);
                _rows[file.RelativePath] = row;
                Files.Add(row);
            }
        }
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Files.Remove(_rows[gone]);
            _rows.Remove(gone);
        }
        FilesView.Refresh();
        UpdateDetails(reloadThumbnail: false);

        // Banner: the most important things first.
        var lines = new List<string>();
        var brush = InfoBrush;
        _viewingMilestone = snapshot.ViewingMilestone;
        OnPropertyChanged(nameof(IsViewingMilestone));
        if (_viewingMilestone != null)
        {
            var when = _viewingMilestone.CreatedAt == default ? "" : $" (created {_viewingMilestone.CreatedAt.LocalDateTime:MMM d, yyyy} by {_viewingMilestone.CreatedBy})";
            lines.Add($"Viewing the milestone \u201C{_viewingMilestone.Name}\u201D{when}: files show how they were then, and nothing can be checked out or in. Click Return to Latest to keep working.");
            brush = WarningBrush;
        }
        if (snapshot.ConfigProblem != null)
        {
            lines.Add(snapshot.ConfigProblem);
            brush = ErrorBrush;
        }
        if (snapshot.IsOffline)
        {
            lines.Add("Offline: showing the last known status. " + snapshot.ConnectionProblem);
            if (brush != ErrorBrush) brush = WarningBrush;
        }
        else if (snapshot.ConnectionProblem != null)
        {
            lines.Add("Couldn't read check-outs from the vault: " + snapshot.ConnectionProblem);
            if (brush != ErrorBrush) brush = WarningBrush;
        }
        HasSetupProblems = snapshot.SetupProblems.Count > 0;
        if (HasSetupProblems)
        {
            lines.Add("Admin: " + string.Join(" ", snapshot.SetupProblems) + " Click Update Vault Setup to fix it.");
            if (brush != ErrorBrush) brush = WarningBrush;
        }
        _snapshot = snapshot;
        if (snapshot.Storage != null && snapshot.Storage.OverWarning)
        {
            var s = snapshot.Storage;
            lines.Add($"Admin: the vault uses {s.UsedGB:0.0} GB of the free {s.LimitGB:0.#} GB of storage ({s.UsedPercent:0}%), counting every version ever checked in. " +
                      "Download use is on GitHub under Settings, Billing. The README lists ways to save space.");
            if (brush != ErrorBrush) brush = WarningBrush;
        }
        if (snapshot.PendingCheckIns > 0)
        {
            lines.Add($"You have {snapshot.PendingCheckIns} check-in(s) saved on this PC that haven't reached the vault. Select the files and click Check In to send them.");
            if (brush != ErrorBrush) brush = WarningBrush;
        }
        var unlocked = snapshot.Files.Count(f => f.State == FileState.ModifiedWithoutCheckOut);
        if (unlocked > 0)
        {
            lines.Add($"{unlocked} file(s) were edited without being checked out. Check them out now (your edits are kept), or use Undo Check Out to throw the edits away.");
            if (brush != ErrorBrush) brush = WarningBrush;
        }
        if (snapshot.UpdatesAvailable > 0)
        {
            lines.Add($"The vault has {snapshot.UpdatesAvailable} new check-in(s). Click Get Latest.");
        }
        SetBanner(string.Join("\n", lines), brush);

        var mine = snapshot.Files.Count(f => f.State == FileState.CheckedOutByMe);
        var storage = snapshot.Storage != null ? $"  •  storage {StorageUsage.FormatBytes(snapshot.Storage.AllVersionsBytes)} of {snapshot.Storage.LimitGB:0.#} GB" : "";
        var notDownloaded = snapshot.Files.Count(f => f.NotDownloaded);
        var partial = snapshot.DownloadFolders != null ? $"  •  {notDownloaded} not downloaded" : "";
        InfoText = $"{_vault!.UserDisplayName}{(IsAdmin ? " (admin)" : "")}  •  {mine} checked out to you  •  {snapshot.Files.Count} files{partial}{storage}  •  refreshed {snapshot.RetrievedAt:h:mm tt}";
        OnPropertyChanged(nameof(IsAdmin));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>Search looks at the path and every SolidWorks property (part number, description, material, ...).</summary>
    private bool MatchesSearch(FileRowViewModel row)
    {
        bool Has(string? text) => text != null && text.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0;
        return Has(row.Path) || (row.File.Index?.Properties.Values.Any(Has) ?? false);
    }

    private bool MatchesFilter(FileRowViewModel row)
    {
        if (SearchText.Length > 0 && !MatchesSearch(row)) return false;
        var f = row.File;
        return FilterIndex switch
        {
            1 => f.State == FileState.CheckedOutByMe,
            2 => f.State == FileState.CheckedOutByOther,
            3 => f.State is FileState.ModifiedWithoutCheckOut or FileState.NotInVault or FileState.Modified or FileState.MissingLocally || f.IsOutdated,
            4 => f.IsOutdated,
            5 => f.State == FileState.Frozen,
            6 => f.NotDownloaded,
            _ => true,
        };
    }

    // ------------------------------------------------------------ commands

    private List<FileRowViewModel> CheckOutTargets() =>
        _selected.Where(r => r.File.IsLockable && r.File.State is FileState.Available or FileState.ModifiedWithoutCheckOut).ToList();

    /// <summary>The selection, or with nothing selected, everything you could check in.</summary>
    private List<FileRowViewModel> CheckInTargets()
    {
        static bool CanCheckIn(FileState s) => s is FileState.CheckedOutByMe or FileState.NotInVault or FileState.Modified or FileState.MissingLocally;
        // With nothing selected, leave out team settings for non-admins: they couldn't check them in anyway.
        bool Offered(FileRowViewModel r) => _selected.Count > 0 || IsAdmin || !string.Equals(r.Path, "pdm.json", StringComparison.OrdinalIgnoreCase);
        var source = _selected.Count > 0 ? _selected : Files;
        return source.Where(r => (CanCheckIn(r.File.State) || (_selected.Count > 0 && r.File.State == FileState.ModifiedWithoutCheckOut)) && Offered(r)).ToList();
    }

    private List<FileRowViewModel> FreezeTargets() =>
        _selected.Where(r => r.File.IsLockable && !r.File.HasLocalChanges && r.File.State is FileState.Available or FileState.CheckedOutByMe).ToList();

    private List<FileRowViewModel> UnfreezeTargets() =>
        _selected.Where(r => r.File.State == FileState.Frozen).ToList();

    private Task FreezeAsync()
    {
        var targets = FreezeTargets().Select(r => r.Path).ToList();
        var request = _dialogs.AskCheckIn("Freeze (admin)", "Freeze", targets, "Released for production",
            allowKeepCheckedOut: false, prompt: "Reason (required): why are these frozen?");
        if (request == null) return Task.CompletedTask;
        return RunAsync("Freezing…", async (vault, progress) =>
        {
            var result = await vault.FreezeAsync(targets, request.Comment, progress);
            if (result.Succeeded.Any(p => p.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase)))
            {
                result.Notes.Add("Tip: freeze drawings from the SolidWorks pane instead, and a PDF of each revision is saved and frozen too.");
            }
            return Report(result, "Froze");
        });
    }

    private Task RequestCheckInAsync()
    {
        var row = SingleSelected;
        if (row == null) return Task.CompletedTask;
        var request = _dialogs.AskCheckIn("Request Check-In (admin)", "Request", new[] { row.Path }, "Needed by another teammate",
            allowKeepCheckedOut: false, prompt: $"Note to {row.CheckedOutBy} (required): why do you need it?");
        if (request == null) return Task.CompletedTask;
        return RunAsync("Requesting check-in…", async (vault, progress) =>
            Report(await vault.RequestCheckInAsync(row.Path, request.Comment, progress), "Requested check-in of"));
    }

    private Task UnfreezeAsync()
    {
        var targets = UnfreezeTargets().Select(r => r.Path).ToList();
        if (!_dialogs.Confirm("Unfreeze (admin)", $"Unfreeze {PathList.Format(targets)}?\n\nAnyone will be able to check them out and change them again. The team will be told in Discord."))
        {
            return Task.CompletedTask;
        }
        return RunAsync("Unfreezing…", async (vault, progress) =>
            Report(await vault.UnfreezeAsync(targets, progress), "Unfroze"));
    }

    private List<FileRowViewModel> UndoTargets() =>
        _selected.Where(r => r.File.State is FileState.CheckedOutByMe or FileState.ModifiedWithoutCheckOut or FileState.Modified or FileState.MissingLocally).ToList();

    private Task GetLatestAsync() => RunAsync("Getting latest…", async (vault, progress) =>
    {
        var result = await vault.GetLatestAsync(progress);
        return result.CheckInsReceived == 0
            ? "Already up to date."
            : $"Got latest: {result.UpdatedFiles.Count} file(s) updated.";
    });

    private Task CheckOutAsync()
    {
        var targets = CheckOutTargets().Select(r => r.Path).ToList();

        // Changing a part usually means updating its drawing too: offer the drawings that are free.
        var drawings = CheckOutTargets().SelectMany(r => r.File.Drawings).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => !targets.Contains(d, StringComparer.OrdinalIgnoreCase))
            .Where(d => _rows.TryGetValue(d, out var row) && row.File.State == FileState.Available)
            .ToList();
        if (drawings.Count > 0 && _dialogs.Confirm("Check Out",
                $"Also check out {(drawings.Count == 1 ? "its drawing" : "their drawings")}?\n\n{string.Join("\n", drawings.Select(d => "\u2022 " + d))}\n\n" +
                "Changes to a part usually need the drawing updated too. Click Cancel to check out only what you selected."))
        {
            targets.AddRange(drawings);
        }

        return RunAsync("Checking out…", async (vault, progress) =>
            Report(await vault.CheckOutAsync(targets, progress), "Checked out"));
    }

    private async Task CheckInAsync()
    {
        var targets = CheckInTargets().Select(r => r.Path).ToList();
        if (_vault == null || IsBusy) return;
        var exportWarning = await _vault.GetExportWarningAsync(targets);
        if (exportWarning != null && !_dialogs.Confirm("Check in exports?", exportWarning)) return;
        var team = _vault?.Config.SolidWorksVersion;
        var footnote = team != null
            ? $"Close the files in SolidWorks first. Only check in files saved in SolidWorks {team}: newer versions can't be opened by the team."
            : null;
        var request = _dialogs.AskCheckIn("Check In", "Check In", targets, "", allowKeepCheckedOut: true, footnote: footnote);
        if (request == null) return;
        await RunAsync("Checking in…", async (vault, progress) =>
            Report(await vault.CheckInAsync(targets, request.Comment, request.KeepCheckedOut, progress), "Checked in"));
    }

    private Task UndoCheckOutAsync()
    {
        var targets = UndoTargets().Select(r => r.Path).ToList();
        var edited = UndoTargets().Where(r => r.File.HasLocalChanges).Select(r => r.Name).ToList();
        var message = $"Undo check-out of {PathList.Format(targets)}?";
        if (edited.Count > 0)
        {
            message += $"\n\nYour changes to {PathList.Format(edited)} will be thrown away and replaced with the vault version. " +
                       $"(A backup copy is kept in {FileUtil.BackupRoot}.)";
        }
        if (!_dialogs.Confirm("Undo Check Out", message)) return Task.CompletedTask;
        return RunAsync("Undoing check-out…", async (vault, progress) =>
            Report(await vault.UndoCheckOutAsync(targets, progress), "Undid check-out"));
    }

    private Task BreakLockAsync()
    {
        var row = SingleSelected;
        if (row?.File.Lock == null) return Task.CompletedTask;
        var message = $"Break {row.CheckedOutBy}'s check-out of {row.Name}?\n\n" +
                      "Only do this if they're unreachable or the check-out is stale. Any changes they haven't checked in won't be in the vault, " +
                      "and they'll need to check the file out again before checking in. The team will be told in Discord.";
        if (!_dialogs.Confirm("Break Check-Out (admin)", message)) return Task.CompletedTask;
        return RunAsync("Breaking check-out…", async (vault, _) =>
            Report(await vault.BreakLockAsync(row.Path), "Broke check-out"));
    }

    /// <summary>Called by the History window.</summary>
    public Task RestoreAsync(string path, FileVersion version, string comment) =>
        RunAsync($"Restoring version {version.Version}…", async (vault, progress) =>
            Report(await vault.RestoreVersionAsync(path, version, comment, progress), $"Restored version {version.Version} of"));

    private void ShowHistory()
    {
        if (SingleSelected != null) _dialogs.ShowHistory(SingleSelected.Path);
    }

    private void OpenSelected()
    {
        if (_vault == null || SingleSelected == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_vault.ToFullPath(SingleSelected.Path)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage("Open", "Couldn't open the file: " + ex.Message, isWarning: true);
        }
    }

    private void ShowInExplorer()
    {
        if (_vault == null) return;
        var args = SingleSelected?.File.ExistsLocally == true
            ? $"/select,\"{_vault.ToFullPath(SingleSelected.Path)}\""
            : $"\"{_vault.VaultRoot}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private async Task OpenSettingsAsync()
    {
        var settings = UserSettings.Load();
        var signedInAs = _vault == null ? "No vault open"
            : _vault.GitHubLogin == null ? "Not verified yet (are you online and signed in to GitHub?)"
            : $"{_vault.GitHubLogin}{(_vault.IsAdmin ? " (vault admin)" : "")}, verified with GitHub";
        var currentFolders = _snapshot?.DownloadFolders;
        var request = _dialogs.AskSettings(settings.VaultPath, signedInAs, settings.ExplorerMenu, VaultFolders(), currentFolders);
        if (request == null) return;

        var pathChanged = !string.Equals(settings.VaultPath.TrimEnd('\\'), request.VaultPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        settings.VaultPath = request.VaultPath;
        settings.ExplorerMenu = request.ExplorerMenu;
        settings.Save();
        App.UpdateExplorerMenu(settings);

        if (pathChanged || _vault == null)
        {
            await OpenVaultAsync(request.VaultPath);
        }
        else if (!SameFolders(currentFolders, request.DownloadFolders))
        {
            await RunAsync("Updating download folders…", async (vault, progress) =>
            {
                await vault.SetDownloadFoldersAsync(request.DownloadFolders, progress);
                return request.DownloadFolders == null ? "Downloading everything." : $"Downloading only: {string.Join(", ", request.DownloadFolders)}.";
            });
        }
    }

    private static bool SameFolders(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a == null || b == null ? a == b : a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x), StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<Milestone>> LoadMilestonesAsync() =>
        _vault?.GetMilestonesAsync() ?? Task.FromResult<IReadOnlyList<Milestone>>(Array.Empty<Milestone>());

    /// <summary>Called by the Milestones window. Returns true if it was created.</summary>
    public async Task<bool> CreateMilestoneAsync(string name, string description)
    {
        if (_vault == null) return false;
        try
        {
            var milestone = await _vault.CreateMilestoneAsync(name, description);
            StatusText = $"Created milestone \u201C{milestone.Name}\u201D.";
            return true;
        }
        catch (PdmException ex)
        {
            _dialogs.ShowError(ex);
            return false;
        }
    }

    public Task ViewMilestoneAsync(Milestone milestone) =>
        RunAsync($"Opening milestone \u201C{milestone.Name}\u201D\u2026", async (vault, progress) =>
        {
            await vault.ViewMilestoneAsync(milestone.Tag, progress);
            return $"Viewing milestone \u201C{milestone.Name}\u201D (read-only).";
        });

    private Task UpdateVaultSetupAsync()
    {
        if (!_dialogs.Confirm("Update Vault Setup (admin)",
                "Install (or repair) the vault guard and the 3D preview storage rule in the vault?\n\n" +
                "The vault guard runs on GitHub after every push. It undoes pushes that break the vault rules (for example a non-admin " +
                "changing pdm.json or a frozen file, even with Git commands) and posts to Discord."))
        {
            return Task.CompletedTask;
        }
        return RunAsync("Updating vault setup\u2026", async (vault, progress) => Report(await vault.UpdateVaultSetupAsync(progress), "Updated"));
    }

    private Task ReturnToLatestAsync() =>
        RunAsync("Returning to the latest files\u2026", async (vault, progress) =>
        {
            await vault.ReturnToLatestAsync(progress);
            return "Back on the latest files.";
        });

    public Task<IReadOnlyList<FileVersion>> LoadHistoryAsync(string path) =>
        _vault?.GetHistoryAsync(path) ?? Task.FromResult<IReadOnlyList<FileVersion>>(Array.Empty<FileVersion>());

    // ------------------------------------------------------------ plumbing

    /// <summary>Runs a vault command with busy state and error dialogs, then refreshes the view.</summary>
    private async Task RunAsync(string busyText, Func<VaultService, IProgress<string>, Task<string>> operation)
    {
        if (_vault == null || IsBusy) return;
        IsBusy = true;
        StatusText = busyText;
        var progress = new Progress<string>(s => StatusText = s);
        var finalStatus = "Ready";
        try
        {
            finalStatus = await operation(_vault, progress);
        }
        catch (PdmException ex)
        {
            finalStatus = ex.Title;
            _dialogs.ShowError(ex);
        }
        catch (Exception ex)
        {
            finalStatus = "Error";
            _dialogs.ShowError(new PdmException("Something unexpected went wrong: " + ex.Message, ex.ToString()));
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync(quiet: true);
        StatusText = finalStatus;
    }

    /// <summary>Shows failures and warnings from a multi-file command; returns the status-bar text.</summary>
    private string Report(OperationResult result, string verb)
    {
        var status = result.Succeeded.Count > 0
            ? $"{verb} {PathList.Format(result.Succeeded)}."
            : "Nothing was done.";

        if (result.Failures.Count == 1 && result.Succeeded.Count == 0 && result.Warnings.Count == 0)
        {
            _dialogs.ShowError(result.Failures[0].Error);
            return result.Failures[0].Error.Title;
        }

        var sections = new List<string>();
        if (result.Failures.Count > 0)
        {
            sections.Add("These files had problems:\n" + string.Join("\n", result.Failures.Select(f => $"• {f.Error.Message}")));
        }
        if (result.Warnings.Count > 0) sections.Add(string.Join("\n", result.Warnings.Select(w => "• " + w)));
        if (result.Notes.Count > 0) sections.Add(string.Join("\n", result.Notes));

        if (sections.Count > 0)
        {
            var header = result.Succeeded.Count > 0 ? status + "\n\n" : "";
            _dialogs.ShowMessage(
                result.Failures.Count > 0 ? "Some files weren't processed" : "Done",
                header + string.Join("\n\n", sections),
                isWarning: result.Failures.Count > 0 || result.Warnings.Count > 0);
        }
        return status;
    }

    private void SetBanner(string text, Brush brush)
    {
        BannerText = text;
        BannerBrush = brush;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
