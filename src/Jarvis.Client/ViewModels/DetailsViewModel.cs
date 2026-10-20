using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Jarvis.Client.ViewModels;

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

    private System.Windows.Media.Media3D.Model3D? _model;
    private bool _show3D;
    private bool _loading3D;

    /// <summary>Full path of the 3D preview (STL) saved by the add-in, if there is one on this PC.</summary>
    public string? PreviewPath { get; set; }
    public System.DateTime PreviewWritten { get; set; }
    public bool HasPreviewFile => PreviewPath != null;

    public System.Windows.Media.Media3D.Model3D? Model
    {
        get => _model;
        set => Set(ref _model, value);
    }

    /// <summary>Show the rotatable 3D preview instead of the picture.</summary>
    public bool Show3D
    {
        get => _show3D;
        set
        {
            if (Set(ref _show3D, value)) OnPropertyChanged(nameof(ShowPicture));
        }
    }

    public bool ShowPicture => !Show3D;

    public bool Loading3D
    {
        get => _loading3D;
        set => Set(ref _loading3D, value);
    }

    public string PreviewNote { get; set; } = "";

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
