using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows;
using Jarvis.Client.Services;
using Jarvis.Core.Errors;
using Microsoft.Win32;

namespace Jarvis.Client.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(string vaultPath, string signedInAs, IReadOnlyList<string> folders, IReadOnlyList<string>? downloadFolders)
    {
        InitializeComponent();
        VaultPathBox.Text = vaultPath;
        SignedInText.Text = signedInAs;

        var chosen = new HashSet<string>(downloadFolders ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Union(chosen, StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            FolderList.Items.Add(new CheckBox { Content = folder, IsChecked = chosen.Contains(folder), Margin = new Thickness(2) });
        }
        NoFoldersText.Visibility = FolderList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadSomeRadio.IsChecked = downloadFolders != null;
        DownloadAllRadio.IsChecked = downloadFolders == null;
        ShowAddin();
    }

    private void ShowAddin()
    {
        var installed = SolidWorksAddinSetup.IsAddinInstalled;
        var solidWorks = SolidWorksAddinSetup.IsSolidWorksInstalled();
        AddinText.Text = installed
            ? "\u2713 Installed. In SolidWorks, Jarvis is in the Task Pane on the right."
            : solidWorks
                ? "Not installed. It lets you check files out and in from SolidWorks. Close SolidWorks first; Windows asks for permission once."
                : "SolidWorks isn't installed on this PC.";
        AddinButton.Visibility = !installed && solidWorks ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnInstallAddin(object sender, RoutedEventArgs e)
    {
        AddinButton.IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        AddinText.Text = "Downloading and installing. Click Yes when Windows asks for permission\u2026";
        try
        {
            await SolidWorksAddinSetup.InstallLatestAsync();
            ShowAddin();
        }
        catch (PdmException ex)
        {
            ShowAddin();
            MessageBox.Show(this, ex.Message, "SolidWorks add-in", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            AddinButton.IsEnabled = true;
            Cursor = null;
        }
    }

    public string VaultPath => VaultPathBox.Text.Trim();

    /// <summary>Folders to download, or null for everything (also when "only these" is picked with none ticked).</summary>
    public IReadOnlyList<string>? DownloadFolders
    {
        get
        {
            if (DownloadSomeRadio.IsChecked != true) return null;
            var picked = FolderList.Items.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => c.Content.ToString()!).ToList();
            return picked.Count > 0 ? picked : null;
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the vault folder" };
        if (Directory.Exists(VaultPath)) dialog.InitialDirectory = VaultPath;
        if (dialog.ShowDialog(this) == true) VaultPathBox.Text = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(Path.Combine(VaultPath, ".git")))
        {
            var answer = MessageBox.Show(this,
                $"{VaultPath} doesn't look like a vault (no .git folder). Save anyway?",
                "Settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
    }
}
