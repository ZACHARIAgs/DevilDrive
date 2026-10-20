using System.Collections.Generic;
using CapstonePdm.Core.Errors;

namespace CapstonePdm.Client.ViewModels;

public sealed class CheckInRequest
{
    public CheckInRequest(string comment, bool keepCheckedOut)
    {
        Comment = comment;
        KeepCheckedOut = keepCheckedOut;
    }

    public string Comment { get; }
    public bool KeepCheckedOut { get; }
}

public sealed class SettingsRequest
{
    public SettingsRequest(string vaultPath, bool explorerMenu, IReadOnlyList<string>? downloadFolders)
    {
        VaultPath = vaultPath;
        ExplorerMenu = explorerMenu;
        DownloadFolders = downloadFolders;
    }

    public string VaultPath { get; }
    public bool ExplorerMenu { get; }
    /// <summary>Folders to download; null means everything.</summary>
    public IReadOnlyList<string>? DownloadFolders { get; }
}

/// <summary>A vault (one group's) in the vault dropdown.</summary>
public sealed class VaultChoice
{
    public VaultChoice(string folder, string name)
    {
        Folder = folder;
        Name = name;
    }

    public string Folder { get; }
    public string Name { get; }
    public override string ToString() => Name;
}

/// <summary>Everything the view model needs from windows, so it stays testable and UI-free.</summary>
public interface IDialogs
{
    void ShowError(PdmException error);
    void ShowMessage(string title, string message, bool isWarning);
    bool Confirm(string title, string message);
    CheckInRequest? AskCheckIn(string title, string okText, IReadOnlyList<string> files, string defaultComment, bool allowKeepCheckedOut, string? prompt = null, string? footnote = null);
    /// <summary>The Vaults window: returns the vault to open (or null) and whether the list changed.</summary>
    (string? OpenFolder, bool Changed) ShowVaults();
    SettingsRequest? AskSettings(string vaultPath, string signedInAs, bool explorerMenu, IReadOnlyList<string> folders, IReadOnlyList<string>? downloadFolders);
    void ShowHistory(string relativePath);
    void ShowMilestones();
    /// <summary>A small message in the corner of the screen that goes away by itself.</summary>
    void Notify(string message);
    void Show3D(string title, System.Windows.Media.Media3D.Model3D model);
}
