using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CapstonePdm.Client.ViewModels;

public sealed class PropertyRow
{
    public PropertyRow(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string Value { get; }
}

/// <summary>The details panel for the selected file: preview, state, properties and references.</summary>
public sealed class DetailsViewModel : ViewModelBase
{
    private BitmapSource? _thumbnail;
    private bool _loadingThumbnail;

    public DetailsViewModel(FileRowViewModel row)
    {
        Row = row;
    }

    public FileRowViewModel Row { get; }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (Set(ref _thumbnail, value)) OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => Thumbnail != null;

    public bool LoadingThumbnail
    {
        get => _loadingThumbnail;
        set => Set(ref _loadingThumbnail, value);
    }

    public string Name => Row.Name;
    public string Folder => Row.Folder;
    public string StateText => Row.StateText;
    public Brush StateBrush => Row.StateBrush;

    /// <summary>Key facts, e.g. version, last check-in, who has it.</summary>
    public IReadOnlyList<PropertyRow> Facts { get; set; } = new List<PropertyRow>();

    /// <summary>SolidWorks custom properties (from the vault index).</summary>
    public IReadOnlyList<PropertyRow> Properties { get; set; } = new List<PropertyRow>();
    public bool HasProperties => Properties.Count > 0;

    public IReadOnlyList<string> Contains { get; set; } = new List<string>();
    public bool HasContains => Contains.Count > 0;

    public IReadOnlyList<string> WhereUsed { get; set; } = new List<string>();
    public bool HasWhereUsed => WhereUsed.Count > 0;

    /// <summary>Problems worth a red line: references outside the vault, saved in a too-new SolidWorks.</summary>
    public IReadOnlyList<string> Warnings { get; set; } = new List<string>();
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>When the reference/property information was last refreshed by the SolidWorks add-in.</summary>
    public string IndexNote { get; set; } = "";
}
