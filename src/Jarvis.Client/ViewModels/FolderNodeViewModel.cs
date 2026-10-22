using System.Collections.ObjectModel;

namespace Jarvis.Client.ViewModels;

/// <summary>A folder in the folder tree. The root ("" path) is the whole vault.</summary>
public sealed class FolderNodeViewModel : ViewModelBase
{
    private bool _isExpanded;
    private bool _isSelected;
    private int _fileCount;

    public FolderNodeViewModel(string name, string path)
    {
        Name = name;
        Path = path;
    }

    public string Name { get; }
    /// <summary>Vault-relative, "/" separated; "" for the vault itself.</summary>
    public string Path { get; }
    public bool IsRoot => Path.Length == 0;
    public ObservableCollection<FolderNodeViewModel> Children { get; } = new ObservableCollection<FolderNodeViewModel>();

    /// <summary>Files in this folder and its subfolders.</summary>
    public int FileCount
    {
        get => _fileCount;
        set
        {
            if (Set(ref _fileCount, value)) OnPropertyChanged(nameof(CountText));
        }
    }

    public string CountText => FileCount == 0 ? "" : FileCount.ToString();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
