using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace CapstonePdm.Client.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(string vaultPath, string signedInAs, bool explorerMenu)
    {
        InitializeComponent();
        VaultPathBox.Text = vaultPath;
        SignedInText.Text = signedInAs;
        ExplorerMenuBox.IsChecked = explorerMenu;
    }

    public string VaultPath => VaultPathBox.Text.Trim();
    public bool ExplorerMenu => ExplorerMenuBox.IsChecked == true;

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
