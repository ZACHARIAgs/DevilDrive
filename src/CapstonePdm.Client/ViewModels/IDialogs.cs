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
    public SettingsRequest(string vaultPath, bool explorerMenu)
    {
        VaultPath = vaultPath;
        ExplorerMenu = explorerMenu;
    }

    public string VaultPath { get; }
    public bool ExplorerMenu { get; }
}

/// <summary>Everything the view model needs from windows, so it stays testable and UI-free.</summary>
public interface IDialogs
{
    void ShowError(PdmException error);
    void ShowMessage(string title, string message, bool isWarning);
    bool Confirm(string title, string message);
    CheckInRequest? AskCheckIn(string title, string okText, IReadOnlyList<string> files, string defaultComment, bool allowKeepCheckedOut, string? prompt = null, string? footnote = null);
    SettingsRequest? AskSettings(string vaultPath, string signedInAs, bool explorerMenu);
    void ShowHistory(string relativePath);
    void ShowMilestones();
    void Show3D(string title, System.Windows.Media.Media3D.Model3D model);
}
