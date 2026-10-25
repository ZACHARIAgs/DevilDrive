using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jarvis.Client.ViewModels;
using Jarvis.Core.Errors;

namespace Jarvis.Client.Views;

public partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;
    }

    /// <summary>Open the Vaults window once the vault is loaded.</summary>
    public bool ShowVaultsOnLoad { get; set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.InitializeAsync();
        if (ShowVaultsOnLoad && _vm.ManageVaultsCommand.CanExecute(null)) _vm.ManageVaultsCommand.Execute(null);
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

    // ------------------------------------------------------------ folder tree and dragging files onto it

    private const string DragFormat = "JarvisVaultFiles";
    private Point? _dragStart;
    private FileRowViewModel? _pressedSelectedRow;

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node) _vm.SelectedFolder = node.Path;
    }

    /// <summary>Right-clicking a folder selects it first, so its menu acts on that folder.</summary>
    private void OnFolderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private static TreeViewItem? ItemUnder(object source)
    {
        var element = source as DependencyObject;
        while (element != null && element is not TreeViewItem) element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        return element as TreeViewItem;
    }

    private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _pressedSelectedRow = null;
        if (e.OriginalSource is not FrameworkElement { DataContext: FileRowViewModel row }) return;
        _dragStart = e.GetPosition(FilesGrid);
        // Pressing on a row that's already part of a multi-selection: keep the selection so all of them can be dragged.
        if (FilesGrid.SelectedItems.Contains(row) && FilesGrid.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _pressedSelectedRow = row;
            e.Handled = true;
        }
    }

    private void OnGridMouseUp(object sender, MouseButtonEventArgs e)
    {
        // A plain click (no drag) on a multi-selection selects just that row, as usual.
        if (_pressedSelectedRow != null) FilesGrid.SelectedItem = _pressedSelectedRow;
        _pressedSelectedRow = null;
        _dragStart = null;
    }

    private void OnGridMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || FilesGrid.SelectedItems.Count == 0) return;
        var now = e.GetPosition(FilesGrid);
        if (System.Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        _pressedSelectedRow = null;
        var paths = FilesGrid.SelectedItems.Cast<FileRowViewModel>().Select(r => r.Path).ToArray();
        DragDrop.DoDragDrop(FilesGrid, new DataObject(DragFormat, paths), DragDropEffects.Move);
    }

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        var target = ItemUnder(e.OriginalSource);
        e.Effects = e.Data.GetDataPresent(DragFormat) && target?.DataContext is FolderNodeViewModel ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not string[] paths || ItemUnder(e.OriginalSource)?.DataContext is not FolderNodeViewModel folder) return;
        e.Handled = true;
        await _vm.MoveFilesAsync(paths, folder.Path);
    }

    // ------------------------------------------------------------ IDialogs

    public void ShowError(PdmException error) => new ErrorWindow(error) { Owner = this }.ShowDialog();

    public void ShowMessage(string title, string message, bool isWarning) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, isWarning ? MessageBoxImage.Warning : MessageBoxImage.Information);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public (bool Removed, Jarvis.Core.Vault.NumberingScheme? Scheme)? AskNumbering(Jarvis.Core.Vault.NumberingScheme? current)
    {
        var dialog = new NumberingWindow(current) { Owner = this };
        return dialog.ShowDialog() == true ? (dialog.Removed, dialog.Result) : null;
    }

    public (string PartNumber, string DownloadedFile)? AskMcMasterPart()
    {
        var dialog = new McMasterWindow { Owner = this };
        return dialog.ShowDialog() == true && dialog.DownloadedFile != null ? (dialog.PartNumber, dialog.DownloadedFile) : null;
    }

    public string? AskText(string title, string prompt, string initial, Func<string, string?> problem)
    {
        var dialog = new TextPromptWindow(title, prompt, initial, problem) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public string? AskFolder(string title, string vaultName, IReadOnlyList<string> folders, string? current, Func<string, string, string> createFolder)
    {
        var dialog = new FolderPickerWindow(title, vaultName, folders, current, createFolder) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Folder : null;
    }

    public CheckInRequest? AskCheckIn(string title, string okText, IReadOnlyList<string> files, string defaultComment, bool allowKeepCheckedOut, string? prompt = null, string? footnote = null)
    {
        var dialog = new CheckInWindow(title, okText, files, defaultComment, allowKeepCheckedOut, prompt, footnote) { Owner = this };
        return dialog.ShowDialog() == true ? new CheckInRequest(dialog.Comment, dialog.KeepCheckedOut) : null;
    }

    public SettingsRequest? AskSettings(string vaultPath, string signedInAs, IReadOnlyList<string> folders, IReadOnlyList<string>? downloadFolders)
    {
        var dialog = new SettingsWindow(vaultPath, signedInAs, folders, downloadFolders) { Owner = this };
        return dialog.ShowDialog() == true ? new SettingsRequest(dialog.VaultPath, dialog.DownloadFolders) : null;
    }

    public (string? OpenFolder, bool Changed) ShowVaults()
    {
        var dialog = new VaultsWindow { Owner = this };
        dialog.ShowDialog();
        return (dialog.OpenFolder, dialog.Changed);
    }

    public void ShowHistory(string relativePath) => new HistoryWindow(_vm, this, relativePath) { Owner = this }.ShowDialog();

    public void Notify(string message) => new NotificationWindow().Say(message, System.TimeSpan.FromSeconds(8));

    public void ShowTeam(string vaultName, string? remoteUrl, string localPath, IReadOnlyDictionary<string, string> displayNames) =>
        new TeamWindow(vaultName, remoteUrl, localPath, displayNames) { Owner = this }.ShowDialog();

    public void ShowMilestones() => new MilestonesWindow(_vm, this) { Owner = this }.ShowDialog();

    public void Show3D(string title, System.Windows.Media.Media3D.Model3D model) => new Viewer3DWindow(title, model) { Owner = this }.Show();
}
