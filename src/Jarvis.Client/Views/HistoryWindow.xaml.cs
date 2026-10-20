using System.Windows;
using System.Windows.Controls;
using Jarvis.Client.ViewModels;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;

namespace Jarvis.Client.Views;

public partial class HistoryWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly IDialogs _dialogs;
    private readonly string _path;

    public HistoryWindow(MainViewModel vm, IDialogs dialogs, string relativePath)
    {
        InitializeComponent();
        _vm = vm;
        _dialogs = dialogs;
        _path = relativePath;
        Title = "History — " + PathList.Name(relativePath);
        HeaderText.Text = relativePath;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        StatusText.Text = "Loading…";
        try
        {
            var versions = await _vm.LoadHistoryAsync(_path);
            VersionsGrid.ItemsSource = versions;
            StatusText.Text = versions.Count == 0
                ? "No versions yet. This file has never been checked in."
                : $"{versions.Count} version(s). The newest is at the top.";
        }
        catch (PdmException ex)
        {
            StatusText.Text = "Couldn't load history.";
            _dialogs.ShowError(ex);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = VersionsGrid.SelectedItem is FileVersion v && VersionsGrid.SelectedIndex > 0 && v.Version > 0;

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (VersionsGrid.SelectedItem is not FileVersion version) return;

        var request = _dialogs.AskCheckIn(
            $"Restore Version {version.Version}",
            "Restore",
            new[] { _path },
            $"Restored version {version.Version}: {version.CommentFirstLine}",
            allowKeepCheckedOut: false);
        if (request == null) return;

        IsEnabled = false;
        await _vm.RestoreAsync(_path, version, request.Comment);
        IsEnabled = true;
        await LoadAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
