using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CapstonePdm.Core.Config;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Vault;
using Microsoft.Win32;

namespace CapstonePdm.Client.Views;

/// <summary>
/// Manage the vaults (one per group) on this PC: switch, add an existing folder, forget one, or
/// download a vault from the GitHub repos the user can access.
/// </summary>
public partial class VaultsWindow : Window
{
    private sealed class Item
    {
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Folder { get; init; } = "";
        public RemoteVault? Remote { get; init; }
    }

    private readonly UserSettings _settings = UserSettings.Load();
    private IReadOnlyList<RemoteVault> _remote = Array.Empty<RemoteVault>();
    private bool _busy;

    /// <param name="findNow">Look for the user's vaults on GitHub right away.</param>
    /// <param name="createNew">Start with the New team vault form.</param>
    public VaultsWindow(bool findNow = false, bool createNew = false)
    {
        InitializeComponent();
        ShowLocal();
        UpdateButtons();
        Loaded += (_, _) =>
        {
            if (createNew) OnNew(this, new RoutedEventArgs());
            else if (findNow) OnFind(this, new RoutedEventArgs());
        };
    }

    /// <summary>The vault to open in the app after the window closes, if any.</summary>
    public string? OpenFolder { get; private set; }

    /// <summary>The list of vaults changed (added or removed).</summary>
    public bool Changed { get; private set; }

    private void ShowLocal()
    {
        LocalList.Items.Clear();
        foreach (var folder in _settings.Vaults)
        {
            var missing = !Directory.Exists(Path.Combine(folder, ".git"));
            var current = string.Equals(folder, _settings.VaultPath, StringComparison.OrdinalIgnoreCase);
            LocalList.Items.Add(new Item
            {
                Title = NameOf(folder) + (current ? "  (open now)" : ""),
                Detail = folder + (missing ? "  \u2014  not found on this PC" : ""),
                Folder = folder,
            });
        }
    }

    private void ShowRemote()
    {
        RemoteList.Items.Clear();
        foreach (var vault in _remote)
        {
            var status = vault.ExistingFolder != null ? $"on this PC: {vault.ExistingFolder}" : $"will download to {vault.LocalPath}";
            RemoteList.Items.Add(new Item
            {
                Title = vault.VaultName + (vault.IsAdmin ? "  (you're an admin)" : ""),
                Detail = $"{vault.FullName}  \u2022  {status}",
                Folder = vault.ExistingFolder ?? vault.LocalPath,
                Remote = vault,
            });
        }
        if (_remote.Count == 0) StatusText.Text = "No vaults found on your GitHub account.";
    }

    private static string NameOf(string folder)
    {
        try
        {
            if (File.Exists(Path.Combine(folder, VaultConfig.FileName))) return VaultConfig.Load(folder).VaultName;
        }
        catch (PdmException)
        {
            // Broken pdm.json: fall back to the folder name.
        }
        return Path.GetFileName(folder);
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var local = LocalList.SelectedItem as Item;
        OpenButton.IsEnabled = !_busy && local != null && Directory.Exists(Path.Combine(local.Folder, ".git"));
        RemoveButton.IsEnabled = !_busy && local != null;
        InviteButton.IsEnabled = !_busy && local != null && Directory.Exists(Path.Combine(local.Folder, ".git"));
        FindButton.IsEnabled = NewButton.IsEnabled = !_busy;
        var remote = RemoteList.SelectedItem as Item;
        DownloadButton.IsEnabled = !_busy && remote != null;
        DownloadButton.Content = remote?.Remote?.ExistingFolder != null ? "Open" : "Download and open";
    }

    private void SetBusy(bool busy, string status = "")
    {
        _busy = busy;
        StatusText.Text = status;
        Cursor = busy ? Cursors.Wait : null;
        UpdateButtons();
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (!(LocalList.SelectedItem is Item item) || !Directory.Exists(Path.Combine(item.Folder, ".git"))) return;
        OpenFolder = item.Folder;
        DialogResult = true;
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a vault folder that's already on this PC" };
        if (dialog.ShowDialog(this) != true) return;
        if (!Directory.Exists(Path.Combine(dialog.FolderName, ".git")))
        {
            MessageBox.Show(this, $"{dialog.FolderName} isn't a vault (there's no .git folder in it). Use \"Find my vaults\" to download one.",
                "Vaults", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.AddVault(dialog.FolderName);
        Save();
        ShowLocal();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (!(LocalList.SelectedItem is Item item)) return;
        var answer = MessageBox.Show(this,
            $"Remove {NameOf(item.Folder)} from the list?\n\nThe files stay in {item.Folder}, and you can add the folder again later. " +
            "Check in anything you have checked out there first, or teammates stay locked out of it.",
            "Vaults", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        _settings.RemoveVault(item.Folder);
        Save();
        ShowLocal();
        UpdateButtons();
    }

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Looking for vaults on your GitHub account\u2026");
        try
        {
            var remote = await VaultCatalog.ListAsync();
            // Mark the ones already on this PC (same repo, wherever the folder is).
            foreach (var folder in _settings.Vaults)
            {
                var url = await VaultCatalog.RemoteUrlOfAsync(folder);
                foreach (var vault in remote.Where(v => VaultCatalog.SameRepo(v.CloneUrl, url))) vault.ExistingFolder = folder;
            }
            _remote = remote;
            SetBusy(false, remote.Count == 0 ? "" : $"Found {remote.Count} vault(s).");
            ShowRemote();
        }
        catch (PdmException ex)
        {
            SetBusy(false, ex.Message);
        }
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (!(RemoteList.SelectedItem is Item { Remote: { } vault })) return;
        if (vault.ExistingFolder != null)
        {
            _settings.AddVault(vault.ExistingFolder);
            Save();
            OpenFolder = vault.ExistingFolder;
            DialogResult = true;
            return;
        }

        var folder = vault.LocalPath;
        var existing = await VaultCatalog.RemoteUrlOfAsync(folder);
        if (VaultCatalog.SameRepo(existing, vault.CloneUrl))
        {
            // Already downloaded (e.g. by Setup) but not in the list yet.
            _settings.AddVault(folder);
            Save();
            OpenFolder = folder;
            DialogResult = true;
            return;
        }

        var answer = MessageBox.Show(this,
            $"Download {vault.VaultName} to {folder}?\n\nEveryone in this group keeps the vault in this same folder, so assemblies find their parts. " +
            "It can take a while for a big vault.",
            "Download vault", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        SetBusy(true, $"Downloading {vault.VaultName}\u2026");
        try
        {
            await VaultCatalog.CloneAsync(vault, folder, new Progress<string>(text => StatusText.Text = text));
            _settings.AddVault(folder);
            Save();
            OpenFolder = folder;
            SetBusy(false);
            DialogResult = true;
        }
        catch (PdmException ex)
        {
            SetBusy(false, "");
            MessageBox.Show(this, ex.Message, "Download vault", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new NewVaultWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } created) return;

        _settings.AddVault(created.Folder);
        Save();
        ShowLocal();
        var invite = MessageBox.Show(this,
            $"{created.VaultName} is ready in {created.Folder} and on GitHub as {created.Repo.FullName} (private).\n\nInvite your teammates now?",
            "New team vault", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
        if (invite == MessageBoxResult.Yes)
        {
            new InviteWindow(created.VaultName, created.Repo.CloneUrl, created.Folder) { Owner = this }.ShowDialog();
        }
        OpenFolder = created.Folder;
        DialogResult = true;
    }

    private async void OnInvite(object sender, RoutedEventArgs e)
    {
        if (_busy || !(LocalList.SelectedItem is Item item)) return;
        var url = await VaultCatalog.RemoteUrlOfAsync(item.Folder);
        string localPath;
        try
        {
            localPath = VaultConfig.Load(item.Folder).LocalPath is { Length: > 0 } configured ? configured : item.Folder;
        }
        catch (PdmException)
        {
            localPath = item.Folder;
        }
        new InviteWindow(NameOf(item.Folder), url, localPath) { Owner = this }.ShowDialog();
    }

    private void Save()
    {
        _settings.Save();
        Changed = true;
    }
}
