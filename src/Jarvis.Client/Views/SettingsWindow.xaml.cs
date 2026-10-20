using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows;
using Microsoft.Win32;

namespace Jarvis.Client.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(string vaultPath, string signedInAs, bool explorerMenu, IReadOnlyList<string> folders, IReadOnlyList<string>? downloadFolders,
        bool previewPane, bool classicMenu)
    {
        InitializeComponent();
        VaultPathBox.Text = vaultPath;
        SignedInText.Text = signedInAs;
        ExplorerMenuBox.IsChecked = explorerMenu;
        PreviewPaneBox.IsChecked = previewPane;
        ClassicMenuBox.IsChecked = classicMenu;

        var chosen = new HashSet<string>(downloadFolders ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Union(chosen, StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            FolderList.Items.Add(new CheckBox { Content = folder, IsChecked = chosen.Contains(folder), Margin = new Thickness(2) });
        }
        NoFoldersText.Visibility = FolderList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadSomeRadio.IsChecked = downloadFolders != null;
        DownloadAllRadio.IsChecked = downloadFolders == null;
    }

    public string VaultPath => VaultPathBox.Text.Trim();
    public bool ExplorerMenu => ExplorerMenuBox.IsChecked == true;
    public bool PreviewPane => PreviewPaneBox.IsChecked == true;
    public bool ClassicMenu => ClassicMenuBox.IsChecked == true;

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
