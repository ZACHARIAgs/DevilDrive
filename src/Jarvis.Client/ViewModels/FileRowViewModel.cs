using System;
using System.Windows.Media;
using Jarvis.Core.Vault;

namespace Jarvis.Client.ViewModels;

/// <summary>One row of the vault view. Updated in place on refresh so selection survives.</summary>
public sealed class FileRowViewModel : ViewModelBase
{
    /// <summary>State colors: [0] for the light look, [1] (lighter, to read on a dark background) for the dark one.</summary>
    private static readonly Brush[] Green = { Frozen(0x2E, 0x7D, 0x32), Frozen(0x4A, 0xDE, 0x80) };
    private static readonly Brush[] Blue = { Frozen(0x15, 0x65, 0xC0), Frozen(0x60, 0xA5, 0xFA) };
    private static readonly Brush[] Red = { Frozen(0xC6, 0x28, 0x28), Frozen(0xF8, 0x71, 0x71) };
    private static readonly Brush[] Orange = { Frozen(0xEF, 0x6C, 0x00), Frozen(0xFB, 0x92, 0x3C) };
    private static readonly Brush[] Purple = { Frozen(0x6A, 0x1B, 0x9A), Frozen(0xC0, 0x84, 0xFC) };
    private static readonly Brush[] Gray = { Frozen(0x75, 0x75, 0x75), Frozen(0x9C, 0xA3, 0xAF) };
    private static readonly Brush[] Slate = { Frozen(0x33, 0x41, 0x55), Frozen(0xCB, 0xD5, 0xE1) };
    private static readonly Brush[] Teal = { Frozen(0x0F, 0x76, 0x6E), Frozen(0x2D, 0xD4, 0xBF) };

    /// <summary>The app is in its dark look (set by ThemeManager; rows are refreshed when it changes).</summary>
    public static bool DarkMode { get; set; }

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

    public Brush StateBrush => StateColors[DarkMode ? 1 : 0];

    private Brush[] StateColors => File.NotDownloaded ? Gray : File.State switch
    {
        FileState.Available => File.IsOutdated ? Gray : Green,
        FileState.CheckedOutByMe => Blue,
        FileState.CheckedOutByOther => Red,
        FileState.ModifiedWithoutCheckOut => Orange,
        FileState.Modified => Orange,
        FileState.NotInVault => Purple,
        FileState.Frozen => Slate,
        FileState.InReview => Teal,
        _ => Gray,
    };

    /// <summary>The state color, faint: the background of the state pill.</summary>
    public Brush StatePillBrush
    {
        get
        {
            var color = ((SolidColorBrush)StateBrush).Color;
            var brush = new SolidColorBrush(Color.FromArgb(DarkMode ? (byte)0x38 : (byte)0x22, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>An ordinary file with nothing to say: "Available" in quiet text instead of a pill.</summary>
    public bool ShowPill => !(File.State == FileState.Available && File.ExistsLocally && !File.IsOutdated && !File.NotDownloaded && File.PinnedVersion == null);

    /// <summary>The state in a couple of words, for the pill. <see cref="StateText"/> is the whole story (the tooltip and the details panel).</summary>
    public string StateShort => File.State switch
    {
        FileState.Available => File.NotDownloaded ? "Not downloaded" : !File.ExistsLocally ? "Not on this PC yet" : File.PinnedVersion != null ? $"Using version {File.PinnedVersion.Version}"
            : File.IsOutdated ? "Newer in vault" : "Available",
        FileState.CheckedOutByMe => File.HasLocalChanges ? "Checked out to you \u00B7 edited" : "Checked out to you",
        FileState.CheckedOutByOther => "Checked out by " + File.CheckedOutBy,
        FileState.Modified => "Edited",
        FileState.ModifiedWithoutCheckOut => "Edited without check-out",
        FileState.NotInVault => "New",
        FileState.MissingLocally => "Deleted on this PC",
        FileState.InReview => "In review",
        FileState.Frozen => File.Revision != null ? $"Frozen \u00B7 Rev {File.Revision}" : "Frozen",
        _ => File.State.ToString(),
    };

    /// <summary>What goes beside the pill: who and why, and anything else worth knowing about the file.</summary>
    public string StateNote
    {
        get
        {
            var notes = new System.Collections.Generic.List<string>();
            if (File.State == FileState.InReview) notes.Add($"by {File.ReviewSubmittedBy}" + (string.IsNullOrWhiteSpace(File.Review?.Note) ? "" : $": {File.Review!.Note}"));
            if (File.State == FileState.Frozen) notes.Add($"by {File.FrozenBy}: {File.Freeze?.Reason}" + (File.FreezeLockMissing ? " (lock missing, freeze again!)" : ""));
            if (File.State != FileState.Available && File.PinnedVersion != null) notes.Add($"using older version {File.PinnedVersion.Version}");
            if (File.State != FileState.Available && File.IsOutdated && File.ExistsLocally) notes.Add("newer version in vault");
            if (File.CheckInRequest != null) notes.Add($"check-in requested by {File.CheckInRequestedBy}");
            if (File.DeleteRequest != null) notes.Add($"delete requested by {File.DeleteRequestedBy}");
            if (File.State != FileState.Available && File.NotDownloaded) notes.Add("not downloaded");
            if (File.ChangedSinceDrawing.Count > 0) notes.Add("drawing may be out of date");
            if (IsWatched) notes.Add("you'll be told when it's free");
            return string.Join("; ", notes);
        }
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
