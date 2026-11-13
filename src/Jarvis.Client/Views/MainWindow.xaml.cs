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
        // Grid columns aren't in the visual tree, so their visibility can't be bound.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.NameIsNumber)) ApplyColumns();
        };
        var settings = Jarvis.Core.Config.UserSettings.Load();
        _hiddenColumns = new HashSet<string>(settings.HiddenColumns ?? (IEnumerable<string>)DefaultHiddenColumns, StringComparer.OrdinalIgnoreCase);
        _compactRows = settings.CompactRows;
        ApplyColumns();
        ThemeManager.Changed += _vm.RefreshColors;
        Closed += (_, _) => ThemeManager.Changed -= _vm.RefreshColors;
    }

    // ------------------------------------------------------------ columns (right-click a column header)

    /// <summary>Out of the way until someone wants them: the list fits the window without scrolling sideways.</summary>
    private static readonly string[] DefaultHiddenColumns = { "Checked Out By", "Since", "Last Check-In Comment", "Checked In By", "Checked In", "Material", "Folder" };
    private const double RowHeight = 34, CompactRowHeight = 26;

    private readonly HashSet<string> _hiddenColumns;
    private bool _compactRows;

    private void ApplyColumns()
    {
        foreach (var column in FilesGrid.Columns)
        {
            if (column.Header is not string header) continue;
            // With file names that are the part numbers, a Part Number column would only repeat the name.
            var hidden = _hiddenColumns.Contains(header) || (column == PartNumberColumn && _vm.NameIsNumber);
            column.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        }
        FilesGrid.MinRowHeight = _compactRows ? CompactRowHeight : RowHeight;
    }

    private void SaveColumns()
    {
        var settings = Jarvis.Core.Config.UserSettings.Load();
        settings.HiddenColumns = _hiddenColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        settings.CompactRows = _compactRows;
        settings.Save();
    }

    /// <summary>Right-click on a column header: choose the columns. Anywhere else in the list: the file menu.</summary>
    private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not System.Windows.Controls.Primitives.DataGridColumnHeader && element is not DataGridRow)
        {
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        if (element is not System.Windows.Controls.Primitives.DataGridColumnHeader) return;
        e.Handled = true;

        var menu = new ContextMenu { PlacementTarget = FilesGrid, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var column in FilesGrid.Columns)
        {
            if (column.Header is not string header || header == "Name" || (column == PartNumberColumn && _vm.NameIsNumber)) continue;
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = !_hiddenColumns.Contains(header), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (item.IsChecked) _hiddenColumns.Remove(header);
                else _hiddenColumns.Add(header);
                ApplyColumns();
                SaveColumns();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var compact = new MenuItem { Header = "Compact rows", IsCheckable = true, IsChecked = _compactRows, StaysOpenOnClick = true, ToolTip = "Shorter rows, to see more files at once" };
        compact.Click += (_, _) =>
        {
            _compactRows = compact.IsChecked;
            ApplyColumns();
            SaveColumns();
        };
        menu.Items.Add(compact);
        var reset = new MenuItem { Header = "Reset columns" };
        reset.Click += (_, _) =>
        {
            _hiddenColumns.Clear();
            _hiddenColumns.UnionWith(DefaultHiddenColumns);
            _compactRows = false;
            ApplyColumns();
            SaveColumns();
        };
        menu.Items.Add(reset);
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------ menus

    /// <summary>The Admin button drops its menu down like a menu bar would.</summary>
    private void OnAdminMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.DataContext = _vm;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// The file menu shows only what can be done with the selection: commands that can't run are left out (the three
    /// everyday ones stay, greyed), a group with nothing to offer goes with them, and no separator is left dangling.
    /// </summary>
    private void OnFileMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu) Tidy(menu.Items);
    }

    private static bool Tidy(ItemCollection items)
    {
        var any = false;
        foreach (var entry in items)
        {
            if (entry is not MenuItem item) continue;
            var show = item.HasItems ? Tidy(item.Items) : item.Tag as string == "keep" || (item.Command?.CanExecute(item.CommandParameter) ?? true);
            item.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            any |= show;
        }
        // A separator stays only between two things that are showing.
        var visible = items.OfType<Control>().ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            if (visible[i] is not Separator separator) continue;
            var before = visible.Take(i).LastOrDefault(c => c is Separator || c.Visibility == Visibility.Visible);
            var after = visible.Skip(i + 1).Any(c => c is MenuItem && c.Visibility == Visibility.Visible);
            separator.Visibility = before is MenuItem && after ? Visibility.Visible : Visibility.Collapsed;
        }
        return any;
    }

    // ------------------------------------------------------------ keyboard

    /// <summary>F1 Help, Ctrl+F to the search box, Esc clears the search. The rest are KeyBindings in the XAML.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            OnHelp(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    /// <summary>Enter opens the selected file (the grid would otherwise move to the next row).</summary>
    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || !_vm.OpenCommand.CanExecute(null)) return;
        _vm.OpenCommand.Execute(null);
        e.Handled = true;
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
        if (target?.DataContext is FolderNodeViewModel)
        {
            if (e.Data.GetDataPresent(DragFormat))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (ItemUnder(e.OriginalSource)?.DataContext is not FolderNodeViewModel folder) return;
        if (e.Data.GetData(DragFormat) is string[] paths)
        {
            e.Handled = true;
            await _vm.MoveFilesAsync(paths, folder.Path);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] externalFiles && externalFiles.Length > 0)
        {
            e.Handled = true;
            await _vm.AddFilesToFolderAsync(externalFiles, folder.Path);
        }
    }

    private void OnGridDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private async void OnGridDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Handled = true;
            await _vm.AddFilesToFolderAsync(files, _vm.SelectedFolder);
        }
    }

    // ------------------------------------------------------------ IDialogs

    public void ShowError(PdmException error)
    {
        Services.AppLog.Error(error);
        new ErrorWindow(error) { Owner = this }.ShowDialog();
    }

    public bool ShowErrorWithFix(PdmException error, string fixText, string fixToolTip)
    {
        Services.AppLog.Error(error);
        var window = new ErrorWindow(error, fixText, fixToolTip) { Owner = this };
        window.ShowDialog();
        return window.FixChosen;
    }

    public void ShowMessage(string title, string message, bool isWarning)
    {
        if (isWarning) Services.AppLog.Write($"WARNING {title}: {message}");
        ModernMessageBox.Show(this, message, title, MessageBoxButton.OK, isWarning ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    public bool Confirm(string title, string message) =>
        ModernMessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

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

    public string? AskSaveFile(string title, string defaultName, string filter)
    {
        // Not in the vault folder: a file saved there would show up as a new file to check in.
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title, FileName = defaultName, Filter = filter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public string? AskFolderOnDisk(string title)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return picker.ShowDialog(this) == true ? picker.FolderName : null;
    }

    public IReadOnlyList<(Jarvis.Core.Vault.ImportItem Item, string? PartNumber, string Description)>? AskImport(IReadOnlyList<(string Path, bool FromElsewhere)> files, string sourceFolder,
        Jarvis.Core.Vault.NumberingScheme? scheme, IReadOnlyList<string> used, IReadOnlyList<string> vaultFolders, Func<string, bool> existsInVault,
        Func<string, (string RelativePath, bool Identical)?> vaultMatch)
    {
        // The SolidWorks pane's import table (Windows Forms), owned by this window.
        System.Windows.Forms.Application.EnableVisualStyles();
        using var form = new Jarvis.SwAddin.ImportForm(files, sourceFolder, scheme, used, vaultFolders, existsInVault, vaultMatch);
        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        try
        {
            return form.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK ? form.Result : null;
        }
        finally
        {
            owner.ReleaseHandle();
        }
    }

    public ExportOptions? AskExport(string what, string defaultName)
    {
        var dialog = new ExportWindow(what, defaultName) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public IReadOnlyList<string>? AskOpenFiles(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = true,
            CheckFileExists = true
        };
        return dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0 ? dialog.FileNames : null;
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

    // ------------------------------------------------------------ updates

    /// <summary>How often an open Jarvis looks for a new version (and once more after the PC wakes up).</summary>
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(3);

    private Jarvis.Core.Updates.ReleaseInfo? _update;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    /// <summary>Shows the toolbar's Update button for a newer version.</summary>
    public void ShowUpdate(Jarvis.Core.Updates.ReleaseInfo release)
    {
        _update = release;
        UpdateLabel.Text = "Update to " + Jarvis.Core.Updates.Updater.Format(release.Version);
        UpdateButton.Visibility = Visibility.Visible;
    }

    /// <summary>Checks for a new version every few hours, and a minute after the PC wakes up, without interrupting anyone.</summary>
    public void StartUpdateChecks()
    {
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = UpdateCheckEvery };
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        // Give the network a minute to come back.
        Dispatcher.InvokeAsync(async () =>
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(1));
            await CheckForUpdateAsync();
        });
    }

    private async System.Threading.Tasks.Task CheckForUpdateAsync()
    {
        if (await Services.AppUpdater.FindNewerAsync() is { } release) ShowUpdate(release);
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_update == null || !Services.AppUpdater.AskToUpdate(this, _update)) return;
        UpdateButton.IsEnabled = false;
        try
        {
            await Services.AppUpdater.InstallAsync(this, _update);
        }
        finally
        {
            UpdateButton.IsEnabled = true; // only if the update didn't go ahead (it restarts the app when it does)
        }
    }

    private void OnHelp(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.Show();

    public void ShowMilestones() => new MilestonesWindow(_vm, this) { Owner = this }.ShowDialog();

    public void Show3D(string title, System.Windows.Media.Media3D.Model3D model) => new Viewer3DWindow(title, model) { Owner = this }.Show();
}
