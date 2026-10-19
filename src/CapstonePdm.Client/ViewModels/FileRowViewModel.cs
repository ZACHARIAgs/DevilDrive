using System;
using System.Windows.Media;
using CapstonePdm.Core.Vault;

namespace CapstonePdm.Client.ViewModels;

/// <summary>One row of the vault view. Updated in place on refresh so selection survives.</summary>
public sealed class FileRowViewModel : ViewModelBase
{
    private static readonly Brush Green = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush Blue = Frozen(0x15, 0x65, 0xC0);
    private static readonly Brush Red = Frozen(0xC6, 0x28, 0x28);
    private static readonly Brush Orange = Frozen(0xEF, 0x6C, 0x00);
    private static readonly Brush Purple = Frozen(0x6A, 0x1B, 0x9A);
    private static readonly Brush Gray = Frozen(0x75, 0x75, 0x75);
    private static readonly Brush Slate = Frozen(0x33, 0x41, 0x55);

    public FileRowViewModel(VaultFile file)
    {
        File = file;
    }

    public VaultFile File { get; private set; }

    public string Path => File.RelativePath;
    public string Name => File.Name;
    public string Folder => File.Folder.Length == 0 ? "(vault root)" : File.Folder.Replace('/', '\\');
    public string StateGlyph => "●";
    public int StateSort => (int)File.State;
    public string? CheckedOutBy => File.State == FileState.Frozen ? "Frozen: " + File.FrozenBy : File.CheckedOutBy;
    public DateTime? CheckedOutSince => File.State == FileState.Frozen ? File.Freeze?.At.LocalDateTime : File.Lock?.LockedAt?.LocalDateTime;
    public int Version => File.Version;
    public string VersionText => File.Version > 0 ? File.Version.ToString() : "—";
    public string LastComment => File.LatestVersion?.CommentFirstLine ?? "";
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
                FileState.Frozen => $"Frozen by {File.FrozenBy}: {File.Freeze?.Reason}" + (File.FreezeLockMissing ? " (lock missing, freeze again!)" : ""),
                _ => File.State.ToString(),
            };
            if (File.IsOutdated && File.ExistsLocally) text += ", newer version in vault";
            return text;
        }
    }

    public Brush StateBrush => File.State switch
    {
        FileState.Available => File.IsOutdated ? Gray : Green,
        FileState.CheckedOutByMe => Blue,
        FileState.CheckedOutByOther => Red,
        FileState.ModifiedWithoutCheckOut => Orange,
        FileState.Modified => Orange,
        FileState.NotInVault => Purple,
        FileState.Frozen => Slate,
        _ => Gray,
    };

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
