using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;

namespace Jarvis.Client.ViewModels;

/// <summary>One filter above the file list ("Checked out to me 3"): click to show only those files.</summary>
public sealed class FilterChip : ViewModelBase
{
    private int _count;
    private bool _isSelected;

    public FilterChip(int index, string label, string toolTip)
    {
        Index = index;
        Label = label;
        ToolTip = toolTip;
    }

    /// <summary>The value of <see cref="MainViewModel.FilterIndex"/> this chip stands for.</summary>
    public int Index { get; }
    public string Label { get; }
    public string ToolTip { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (!Set(ref _count, value)) return;
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    public string CountText => Index == 0 ? "" : Count.ToString();

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) OnPropertyChanged(nameof(IsVisible));
        }
    }

    /// <summary>"All files", the chosen filter and any filter that has files; empty ones stay out of the way.</summary>
    public bool IsVisible => Index == 0 || IsSelected || Count > 0;
}

/// <summary>One check-in in the summary's "Recent check-ins" list.</summary>
public sealed class RecentCheckInRow
{
    public RecentCheckInRow(RecentCheckIn checkIn, Func<string, string> displayName)
    {
        IsNew = checkIn.IsNew;
        Who = checkIn.IsMine ? "You" : displayName(checkIn.Version.Author);
        When = Ago(checkIn.Version.Date);
        Comment = checkIn.Version.CommentFirstLine;
        var names = checkIn.Files.Select(PathList.Name).ToList();
        Files = names.Count == 0 ? "" : names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";
        ToolTip = $"{checkIn.Version.Date.LocalDateTime:ddd MMM d, h:mm tt}" + (checkIn.Files.Count > 0 ? "\n" + string.Join("\n", checkIn.Files.Take(25)) : "");
    }

    public bool IsNew { get; }
    public string Who { get; }
    public string When { get; }
    public string Comment { get; }
    public string Files { get; }
    public bool HasFiles => Files.Length > 0;
    public string ToolTip { get; }

    /// <summary>"just now", "25 min ago", "3 h ago", "yesterday", "Nov 4".</summary>
    internal static string Ago(DateTimeOffset when)
    {
        var age = DateTimeOffset.Now - when;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromHours(24) && when.LocalDateTime.Date == DateTime.Today) return $"{(int)age.TotalHours} h ago";
        if (when.LocalDateTime.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        return when.LocalDateTime.ToString(when.LocalDateTime.Year == DateTime.Today.Year ? "MMM d" : "MMM d, yyyy");
    }
}

/// <summary>
/// What the window shows besides the file list: the filter chips with their counts, and the summary in the
/// details panel while no file is selected (your check-outs, what teammates checked in, storage used).
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<FilterChip> Filters { get; } = new ObservableCollection<FilterChip>
    {
        new FilterChip(0, "All files", "Every file in the folder you picked on the left"),
        new FilterChip(1, "Checked out to me", "Files only you can edit right now"),
        new FilterChip(2, "Checked out by others", "Files a teammate has checked out"),
        new FilterChip(3, "Needs attention", "Edited or new files that aren't checked in, files deleted on this PC, and files with a newer version in the vault"),
        new FilterChip(4, "Newer in vault", "Files a teammate checked in since your last Get Latest"),
        new FilterChip(5, "Frozen", "Released files nobody can change until an admin unfreezes them"),
        new FilterChip(6, "Not downloaded", "Files outside the folders this PC downloads"),
        new FilterChip(7, "In review", "Files waiting for an admin to approve or reject"),
        new FilterChip(8, "Delete requested", "Files someone asked the admins to delete"),
    };

    public ICommand SelectFilterCommand { get; private set; } = null!;
    public ICommand MarkSeenCommand { get; private set; } = null!;
    public ICommand ClearSearchCommand { get; private set; } = null!;

    private void InitializeHomeCommands()
    {
        SelectFilterCommand = new ParameterCommand(p =>
        {
            if (p is FilterChip chip) FilterIndex = chip.Index;
        });
        MarkSeenCommand = new RelayCommand(async () => await MarkSeenAsync(), () => _vault != null && NewCheckInCount > 0);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        Filters[0].IsSelected = true;
    }

    /// <summary>Counts for the chips: across the whole vault, so a filter's number doesn't change with the folder you're in.</summary>
    private void UpdateFilterCounts()
    {
        foreach (var chip in Filters)
        {
            chip.Count = chip.Index == 0 ? Files.Count : Files.Count(r => MatchesFilterIndex(r.File, chip.Index));
            chip.IsSelected = chip.Index == FilterIndex;
        }
    }

    private static bool MatchesFilterIndex(VaultFile f, int index) => index switch
    {
        1 => f.State == FileState.CheckedOutByMe,
        2 => f.State == FileState.CheckedOutByOther,
        3 => f.State is FileState.ModifiedWithoutCheckOut or FileState.NotInVault or FileState.Modified or FileState.MissingLocally || f.IsOutdated,
        4 => f.IsOutdated,
        5 => f.State == FileState.Frozen,
        6 => f.NotDownloaded,
        7 => f.State == FileState.InReview,
        8 => f.DeleteRequest != null,
        _ => true,
    };

    // ------------------------------------------------------------ summary (no file selected)

    /// <summary>True while the details panel shows the summary instead of a file.</summary>
    public bool ShowSummary => _vault != null && Details == null;

    public string SummaryTitle => _vault?.Config.VaultName ?? "";

    public string SummarySelectionHint => _selected.Count > 1
        ? $"{_selected.Count} files selected. Select one to see its preview and details."
        : "Select a file to see its preview and details.";

    public ObservableCollection<string> MyCheckOuts { get; } = new ObservableCollection<string>();
    public bool HasMyCheckOuts => MyCheckOuts.Count > 0;
    public string MyCheckOutsHeader => _myCheckOutCount == 0 ? "Checked out to you" : $"Checked out to you ({_myCheckOutCount})";
    private int _myCheckOutCount;

    public ObservableCollection<RecentCheckInRow> RecentCheckIns { get; } = new ObservableCollection<RecentCheckInRow>();
    public bool HasRecentCheckIns => RecentCheckIns.Count > 0;

    private int _newCheckInCount;

    /// <summary>Teammates' check-ins since this user last clicked "Mark as seen".</summary>
    public int NewCheckInCount
    {
        get => _newCheckInCount;
        private set
        {
            if (!Set(ref _newCheckInCount, value)) return;
            OnPropertyChanged(nameof(HasNewCheckIns));
            OnPropertyChanged(nameof(RecentCheckInsHeader));
        }
    }

    public bool HasNewCheckIns => NewCheckInCount > 0;
    public string RecentCheckInsHeader => NewCheckInCount > 0 ? $"Recent check-ins ({NewCheckInCount} new)" : "Recent check-ins";

    private StorageUsage? _storage;
    private bool _loadingStorage;

    public bool HasStorage => _storage != null;
    public string StorageText => _storage == null ? "" : $"{_storage.Text} used by this vault ({_storage.Versions:N0} stored versions)";
    public double StoragePercent => _storage == null ? 0 : Math.Min(100, _storage.Fraction * 100);
    public bool StorageNearlyFull => _storage?.IsNearlyFull == true;

    private void UpdateSummary()
    {
        var mine = Files.Where(r => r.File.State == FileState.CheckedOutByMe).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _myCheckOutCount = mine.Count;
        MyCheckOuts.Clear();
        foreach (var row in mine.Take(12)) MyCheckOuts.Add(row.Description.Length > 0 ? $"{row.Name}  —  {row.Description}" : row.Name);
        if (mine.Count > 12) MyCheckOuts.Add($"…and {mine.Count - 12} more (use the \"Checked out to me\" filter)");
        OnPropertyChanged(nameof(HasMyCheckOuts));
        OnPropertyChanged(nameof(MyCheckOutsHeader));
        OnPropertyChanged(nameof(SummaryTitle));
        OnPropertyChanged(nameof(ShowSummary));
    }

    /// <summary>After a refresh: what teammates checked in (quick), and how much storage the vault uses (slow the first time, so in the background).</summary>
    private async Task LoadHomeAsync()
    {
        var vault = _vault;
        if (vault == null) return;
        try
        {
            var recent = await vault.GetRecentCheckInsAsync(25);
            if (vault != _vault) return;
            RecentCheckIns.Clear();
            foreach (var checkIn in recent) RecentCheckIns.Add(new RecentCheckInRow(checkIn, vault.Config.DisplayNameFor));
            NewCheckInCount = recent.Count(c => c.IsNew);
            OnPropertyChanged(nameof(HasRecentCheckIns));
        }
        catch (PdmException)
        {
            // The list just stays as it was.
        }

        if (_loadingStorage) return;
        _loadingStorage = true;
        try
        {
            var storage = await Task.Run(() => vault.GetStorageUsageAsync());
            if (vault != _vault || storage == null) return;
            var wasNearlyFull = StorageNearlyFull;
            _storage = storage;
            OnPropertyChanged(nameof(HasStorage));
            OnPropertyChanged(nameof(StorageText));
            OnPropertyChanged(nameof(StoragePercent));
            OnPropertyChanged(nameof(StorageNearlyFull));
            if (storage.IsNearlyFull && !wasNearlyFull && _snapshot != null) Apply(_snapshot); // the banner gains its storage warning
        }
        catch (Exception ex) when (ex is PdmException or InvalidOperationException)
        {
            // No storage figure this time.
        }
        finally
        {
            _loadingStorage = false;
        }
    }

    private async Task MarkSeenAsync()
    {
        if (_vault == null) return;
        await _vault.MarkCheckInsSeenAsync();
        await LoadHomeAsync();
    }
}

/// <summary>A command that gets the clicked item (CommandParameter).</summary>
public sealed class ParameterCommand : ICommand
{
    private readonly Action<object?> _execute;

    public ParameterCommand(Action<object?> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute(parameter);
}
