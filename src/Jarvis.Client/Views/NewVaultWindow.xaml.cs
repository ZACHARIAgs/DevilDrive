using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jarvis.Core.Errors;
using Jarvis.Core.Git;
using Jarvis.Core.Vault;
using Microsoft.Win32;

namespace Jarvis.Client.Views;

/// <summary>A team lead creates their team's vault: a private GitHub repo, set up and downloaded to this PC.</summary>
public partial class NewVaultWindow : Window
{
    private bool _repoEdited;
    private bool _folderEdited;
    private bool _filling;
    private bool _busy;
    private GitHubUser? _user;

    public NewVaultWindow()
    {
        InitializeComponent();
        for (var year = DateTime.Now.Year + 1; year >= 2020; year--) VersionBox.Items.Add(year.ToString(CultureInfo.InvariantCulture));
        VersionBox.Items.Add("No check");
        VersionBox.Text = VaultCreator.DefaultSolidWorksVersion.ToString(CultureInfo.InvariantCulture);
        AddFolderRow("COTS", "Purchased parts: McMaster-Carr, bearings, fasteners (Jarvis puts McMaster parts in COTS/MCMASTER)");
        AddFolderRow("Sandbox", "For trying things out without cluttering the real parts");
        Fill(() =>
        {
            NameBox.Text = "Team Vault";
            RepoBox.Text = VaultCreator.RepoNameFor(NameBox.Text);
            FolderBox.Text = FolderFor(NameBox.Text);
        });
        Loaded += async (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            try
            {
                _user = await GitHubSignIn.GetSignedInUserAsync();
                if (_user == null)
                {
                    StatusText.Text = "You'll be asked to sign in to GitHub when you click Create vault.";
                    return;
                }
                OwnerBox.Items.Add(_user.Login);
                foreach (var org in await (await GitHubApi.SignedInAsync()).GetOrganizationsAsync()) OwnerBox.Items.Add(org);
                if (string.IsNullOrWhiteSpace(OwnerBox.Text)) OwnerBox.Text = _user.Login;
            }
            catch (PdmException ex)
            {
                StatusText.Text = ex.Message;
            }
        };
    }

    /// <summary>One starting folder: ticked by default, name editable.</summary>
    private void AddFolderRow(string name, string hint)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var box = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var nameBox = new TextBox { Text = name, Width = 140, VerticalContentAlignment = VerticalAlignment.Center };
        nameBox.IsEnabled = true;
        box.Checked += (_, _) => nameBox.IsEnabled = true;
        box.Unchecked += (_, _) => nameBox.IsEnabled = false;
        DockPanel.SetDock(box, Dock.Left);
        DockPanel.SetDock(nameBox, Dock.Left);
        row.Children.Add(box);
        row.Children.Add(nameBox);
        row.Children.Add(new TextBlock { Text = hint, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Tag = (box, nameBox);
        FoldersPanel.Children.Add(row);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        AddFolderRow("New Folder", "");
        if (FoldersPanel.Children[FoldersPanel.Children.Count - 1] is DockPanel { Tag: (CheckBox, TextBox nameBox) })
        {
            nameBox.Focus();
            nameBox.SelectAll();
        }
    }

    /// <summary>The ticked starting folders' names.</summary>
    private System.Collections.Generic.List<string> ChosenFolders() =>
        FoldersPanel.Children.OfType<DockPanel>()
            .Select(r => r.Tag is (CheckBox box, TextBox name) && box.IsChecked == true ? name.Text.Trim() : "")
            .Where(n => n.Length > 0).ToList();

    /// <summary>The new vault, once it's created.</summary>
    public NewVaultResult? Result { get; private set; }

    /// <summary>"Club Vault" -> C:\ClubVault</summary>
    private static string FolderFor(string vaultName)
    {
        var words = Regex.Split(vaultName ?? "", @"[^A-Za-z0-9]+").Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1));
        var name = string.Concat(words);
        if (name.Length == 0) name = "Vault";
        if (!name.EndsWith("Vault", StringComparison.OrdinalIgnoreCase)) name += "Vault";
        return @"C:\" + name;
    }

    private void Fill(Action action)
    {
        _filling = true;
        try
        {
            action();
        }
        finally
        {
            _filling = false;
        }
    }

    private void OnNameChanged(object sender, RoutedEventArgs e)
    {
        if (_filling || RepoBox == null || FolderBox == null) return;
        Fill(() =>
        {
            if (!_repoEdited) RepoBox.Text = VaultCreator.RepoNameFor(NameBox.Text);
            if (!_folderEdited) FolderBox.Text = FolderFor(NameBox.Text);
        });
    }

    private void OnRepoChanged(object sender, RoutedEventArgs e)
    {
        if (!_filling) _repoEdited = true;
    }

    private void OnFolderChanged(object sender, RoutedEventArgs e)
    {
        if (!_filling) _folderEdited = true;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where the vault goes on every PC (an empty or new folder)" };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private NewVaultRequest Request()
    {
        var versionText = VersionBox.Text.Trim();
        int? version = int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null;
        if (version == null && !string.Equals(versionText, "No check", StringComparison.OrdinalIgnoreCase)) version = -1; // invalid; Validate explains
        return new NewVaultRequest
        {
            VaultName = NameBox.Text.Trim(),
            Owner = OwnerBox.Text.Trim(),
            RepoName = RepoBox.Text.Trim(),
            SolidWorksVersion = version,
            LocalPath = FolderBox.Text.Trim(),
            Folders = ChosenFolders(),
        };
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        StatusText.Text = status;
        Cursor = busy ? Cursors.Wait : null;
        CreateButton.IsEnabled = !busy;
        NameBox.IsEnabled = OwnerBox.IsEnabled = RepoBox.IsEnabled = VersionBox.IsEnabled = FolderBox.IsEnabled = FoldersPanel.IsEnabled = !busy;
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (_user == null)
            {
                SetBusy(true, "Sign in to GitHub in the window that opens\u2026");
                _user = await GitHubSignIn.SignInAsync();
                if (string.IsNullOrWhiteSpace(OwnerBox.Text)) OwnerBox.Text = _user.Login;
                SetBusy(false, "");
            }

            var request = Request();
            var problem = VaultCreator.Validate(request);
            if (problem != null)
            {
                StatusText.Text = problem;
                return;
            }
            SetBusy(true, "Creating the vault\u2026");
            Result = await VaultCreator.CreateAsync(request, new Progress<string>(text => StatusText.Text = text));
            SetBusy(false, "");
            DialogResult = true;
        }
        catch (PdmException ex)
        {
            SetBusy(false, "");
            new ErrorWindow(ex) { Owner = this }.ShowDialog();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Don't walk away from a half-made vault.
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }
}
