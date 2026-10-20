using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CapstonePdm.Client.ViewModels;
using CapstonePdm.Core.Errors;

namespace CapstonePdm.Client.Views;

public partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;
    }

    /// <summary>A file to select once the vault is loaded ("Show in Jarvis" from File Explorer).</summary>
    public string? PendingSelection { get; set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.InitializeAsync();
        if (PendingSelection == null || _vm.Vault == null) return;
        try
        {
            var relative = _vm.Vault.ToRelativePath(PendingSelection);
            var row = FilesGrid.Items.Cast<FileRowViewModel>().FirstOrDefault(r => string.Equals(r.Path, relative, System.StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                FilesGrid.SelectedItem = row;
                FilesGrid.ScrollIntoView(row);
                FilesGrid.Focus();
            }
        }
        catch (CapstonePdm.Core.Errors.PdmException)
        {
            // Not inside the vault; just show the app.
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SelectedFiles = FilesGrid.SelectedItems.Cast<FileRowViewModel>().ToList();

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only rows, not the header or scrollbars.
        if (e.OriginalSource is FrameworkElement { DataContext: FileRowViewModel } && _vm.OpenCommand.CanExecute(null))
        {
            _vm.OpenCommand.Execute(null);
        }
    }

    // ------------------------------------------------------------ IDialogs

    public void ShowError(PdmException error) => new ErrorWindow(error) { Owner = this }.ShowDialog();

    public void ShowMessage(string title, string message, bool isWarning) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, isWarning ? MessageBoxImage.Warning : MessageBoxImage.Information);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public CheckInRequest? AskCheckIn(string title, string okText, IReadOnlyList<string> files, string defaultComment, bool allowKeepCheckedOut, string? prompt = null, string? footnote = null)
    {
        var dialog = new CheckInWindow(title, okText, files, defaultComment, allowKeepCheckedOut, prompt, footnote) { Owner = this };
        return dialog.ShowDialog() == true ? new CheckInRequest(dialog.Comment, dialog.KeepCheckedOut) : null;
    }

    public SettingsRequest? AskSettings(string vaultPath, string signedInAs, bool explorerMenu, IReadOnlyList<string> folders, IReadOnlyList<string>? downloadFolders,
        bool previewPane, bool classicMenu)
    {
        var dialog = new SettingsWindow(vaultPath, signedInAs, explorerMenu, folders, downloadFolders, previewPane, classicMenu) { Owner = this };
        return dialog.ShowDialog() == true
            ? new SettingsRequest(dialog.VaultPath, dialog.ExplorerMenu, dialog.DownloadFolders, dialog.PreviewPane, dialog.ClassicMenu)
            : null;
    }

    public (string? OpenFolder, bool Changed) ShowVaults()
    {
        var dialog = new VaultsWindow { Owner = this };
        dialog.ShowDialog();
        return (dialog.OpenFolder, dialog.Changed);
    }

    public void ShowHistory(string relativePath) => new HistoryWindow(_vm, this, relativePath) { Owner = this }.ShowDialog();

    public void Notify(string message) => new NotificationWindow().Say(message, System.TimeSpan.FromSeconds(8));

    public void ShowMilestones() => new MilestonesWindow(_vm, this) { Owner = this }.ShowDialog();

    public void Show3D(string title, System.Windows.Media.Media3D.Model3D model) => new Viewer3DWindow(title, model) { Owner = this }.Show();
}
