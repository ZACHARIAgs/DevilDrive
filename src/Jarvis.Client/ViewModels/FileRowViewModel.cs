using System;
using System.Windows.Media;
using Jarvis.Client.Services;
using Jarvis.Core.Vault;

namespace Jarvis.Client.ViewModels;

/// <summary>One row of the vault view. Updated in place on refresh so selection survives.</summary>
public sealed class FileRowViewModel : ViewModelBase
{
    // Light mode brushes (standard dark-tinted colors on white)
    private static readonly Brush Green = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush Blue = Frozen(0x15, 0x65, 0xC0);
    private static readonly Brush Red = Frozen(0xC6, 0x28, 0x28);
    private static readonly Brush Orange = Frozen(0xEF, 0x6C, 0x00);
    private static readonly Brush Purple = Frozen(0x6A, 0x1B, 0x9A);
    private static readonly Brush Gray = Frozen(0x75, 0x75, 0x75);
    private static readonly Brush Slate = Frozen(0x33, 0x41, 0x55);
    private static readonly Brush Teal = Frozen(0x0F, 0x76, 0x6E);

    // Dark mode brushes (high contrast bright colors on dark zinc)
    private static readonly Brush DarkGreen = Frozen(0x4A, 0xDE, 0x80);
    private static readonly Brush DarkBlue = Frozen(0x60, 0xA5, 0xFA);
    private static readonly Brush DarkRed = Frozen(0xF8, 0x71, 0x71);
    private static readonly Brush DarkOrange = Frozen(0xFB, 0x92, 0x3C);
    private static readonly Brush DarkPurple = Frozen(0xC0, 0x84, 0xFC);
    private static readonly Brush DarkGray = Frozen(0xA1, 0xA1, 0xAA);
    private static readonly Brush DarkSlate = Frozen(0x94, 0xA3, 0xB8);
    private static readonly Brush DarkTeal = Frozen(0x2D, 0xD4, 0xBF);

    public FileRowViewModel(VaultFile file)
    {
        File = file;
    }

    public VaultFile File { get; private set; }

    /// <summary>This user asked to be told when the file is free.</summary>
    public bool IsWatched { get; set; }

    public string Path => File.RelativePath;
    public string Name => File.Name;
    public string Folder => File.Folder.Length == 0 ? "(vault root)" : File.Folder.Replace('/', '\\');
    public string StateGlyph => "●";
    public int StateSort => (int)File.State;
    public string? CheckedOutBy => File.State == FileState.Frozen ? "Frozen: " + File.FrozenBy : File.State == FileState.InReview ? "In review: " + File.ReviewSubmittedBy : File.CheckedOutBy;
    public DateTime? CheckedOutSince => File.State == FileState.Frozen ? File.Freeze?.At.LocalDateTime : File.Lock?.LockedAt?.LocalDateTime;
    public int Version => File.Version;
    /// <summary>"7/10": check-ins on this PC / in the vault.</summary>
    public string VersionText => File.VaultVersion > 0 ? File.VersionText : "—";
    /// <summary>The vault has check-ins of this file that this PC doesn't have.</summary>
    public bool IsBehind => File.Version < File.VaultVersion;
    public string LastComment => File.LatestVersion?.CommentFirstLine ?? "";
    public string RevisionText => File.Revision ?? "";
    /// <summary>Part number from the index; SolidWorks files without one show their file name.</summary>
    public string PartNumber => File.Index?.GetProperty(IndexEntry.PartNumberNames) ??
                                (IsSolidWorksFile ? System.IO.Path.GetFileNameWithoutExtension(File.Name) : "");
    public bool HasPartNumber => File.Index?.GetProperty(IndexEntry.PartNumberNames) != null;
    private bool IsSolidWorksFile => System.IO.Path.GetExtension(File.Name).ToLowerInvariant() is ".sldprt" or ".sldasm" or ".slddrw";
    public string Description => File.Index?.GetProperty(IndexEntry.DescriptionNames) ?? "";
    public string Material => File.Index?.GetProperty(IndexEntry.MaterialNames) ?? "";
    public string LastCheckedInBy => File.LatestVersion?.Author ?? "";
    public DateTime? LastCheckIn => File.LatestVersion?.Date.LocalDateTime;

    public string StateText
    {
        get
        {
            var text = File.State switch
            {
                FileState.Available => File.ExistsLocally ? "Available" : "Available (not on this PC yet)",
                FileState.CheckedOutByMe => File.HasLocalChanges ? "Checked out to you (edited)" : "Checked out to you",
                FileState.CheckedOutByOther => "Checked out by " + File.CheckedOutBy,
                FileState.Modified => "Edited (check in to save)",
                FileState.ModifiedWithoutCheckOut => "Edited WITHOUT check-out!",
                FileState.NotInVault => "New, not in vault yet",
                FileState.MissingLocally => "Deleted on this PC",
                FileState.InReview => $"In review (submitted by {File.ReviewSubmittedBy})" + (string.IsNullOrWhiteSpace(File.Review?.Note) ? "" : $": {File.Review!.Note}"),
                FileState.Frozen => (File.Revision != null ? $"Frozen (Rev {File.Revision}) by " : "Frozen by ") + $"{File.FrozenBy}: {File.Freeze?.Reason}" + (File.FreezeLockMissing ? " (lock missing, freeze again!)" : ""),
                _ => File.State.ToString(),
            };
            if (File.PinnedVersion != null) text += $" (using older version {File.PinnedVersion.Version} on this PC; right-click, Return to Latest Version)";
            else if (File.IsOutdated && File.ExistsLocally) text += ", newer version in vault";
            if (File.CheckInRequest != null) text += $" (check-in requested by {File.CheckInRequestedBy})";
            if (File.DeleteRequest != null) text += $" (delete requested by {File.DeleteRequestedBy})";
            if (File.NotDownloaded) text += " (not downloaded: right-click, Download)";
            if (File.ChangedSinceDrawing.Count > 0) text += " (drawing may be out of date)";
            if (IsWatched) text += " (you'll be told when it's free)";
            return text;
        }
    }

    public Brush StateBrush
    {
        get
        {
            var isDark = ThemeManager.IsDark;
            var gray = isDark ? DarkGray : Gray;
            if (File.NotDownloaded) return gray;
            return File.State switch
            {
                FileState.Available => File.IsOutdated ? gray : (isDark ? DarkGreen : Green),
                FileState.CheckedOutByMe => isDark ? DarkBlue : Blue,
                FileState.CheckedOutByOther => isDark ? DarkRed : Red,
                FileState.ModifiedWithoutCheckOut or FileState.Modified => isDark ? DarkOrange : Orange,
                FileState.NotInVault => isDark ? DarkPurple : Purple,
                FileState.Frozen => isDark ? DarkSlate : Slate,
                FileState.InReview => isDark ? DarkTeal : Teal,
                _ => gray,
            };
        }
    }

    public void OnThemeChanged()
    {
        OnPropertyChanged(nameof(StateBrush));
    }

    public void Update(VaultFile file)
    {
        File = file;
        OnPropertyChanged(string.Empty);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
