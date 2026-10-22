using System.Collections.Generic;
using Jarvis.Core.Errors;

namespace Jarvis.Client.ViewModels;

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
    public SettingsRequest(string vaultPath, bool explorerMenu, IReadOnlyList<string>? downloadFolders, bool previewPane, bool classicMenu)
    {
        VaultPath = vaultPath;
        ExplorerMenu = explorerMenu;
        DownloadFolders = downloadFolders;
        PreviewPane = previewPane;
        ClassicMenu = classicMenu;
    }

    public bool PreviewPane { get; }
    /// <summary>Windows 11: use the classic full right-click menu on this PC.</summary>
    public bool ClassicMenu { get; }

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
    SettingsRequest? AskSettings(string vaultPath, string signedInAs, bool explorerMenu, IReadOnlyList<string> folders, IReadOnlyList<string>? downloadFolders,
        bool previewPane, bool classicMenu);
    void ShowHistory(string relativePath);
    void ShowMilestones();
    /// <summary>The vault's team: members, invitations, adding and removing people.</summary>
    void ShowTeam(string vaultName, string? remoteUrl, string localPath, IReadOnlyDictionary<string, string> displayNames);
    /// <summary>A small message in the corner of the screen that goes away by itself.</summary>
    void Notify(string message);
    void Show3D(string title, System.Windows.Media.Media3D.Model3D model);
    /// <summary>Asks for a short text (e.g. a folder name); <paramref name="problem"/> says what's wrong with a value, or null. Null if cancelled.</summary>
    string? AskText(string title, string prompt, string initial, System.Func<string, string?> problem);
    /// <summary>
    /// Picks a folder (vault-relative, "" = top of the vault) from <paramref name="folders"/>; <paramref name="createFolder"/>
    /// makes a new one (parent, name) and returns its path. Null if cancelled.
    /// </summary>
    string? AskFolder(string title, string vaultName, IReadOnlyList<string> folders, string? current, System.Func<string, string, string> createFolder);
}
