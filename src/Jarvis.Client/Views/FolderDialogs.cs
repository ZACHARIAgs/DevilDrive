using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jarvis.Core.Errors;

namespace Jarvis.Client.Views;

/// <summary>Asks for one short line of text (a folder name), with the problem shown under the box.</summary>
internal sealed class TextPromptWindow : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _problem;
    private readonly Func<string, string?> _check;

    public TextPromptWindow(string title, string prompt, string initial, Func<string, string?> check)
    {
        _check = check;
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
        _problem = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)Application.Current.FindResource("DialogButton") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)Application.Current.FindResource("DialogButton") };
        ok.Click += (_, _) => Accept();
        _box.TextChanged += (_, _) => _problem.Text = "";

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_box);
        panel.Children.Add(_problem);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    public string Value => _box.Text.Trim();

    private void Accept()
    {
        var problem = _check(_box.Text);
        if (problem != null)
        {
            _problem.Text = problem;
            return;
        }
        DialogResult = true;
    }
}

/// <summary>Picks a vault folder to move files to, shown as an indented list; can make a new folder too.</summary>
internal sealed class FolderPickerWindow : Window
{
    private sealed class Entry
    {
        public Entry(string path, string label)
        {
            Path = path;
            Label = label;
        }

        public string Path { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    private readonly ListBox _list;
    private readonly string _vaultName;
    private readonly Func<string, string, string> _createFolder;
    private List<string> _folders;

    public FolderPickerWindow(string title, string vaultName, IReadOnlyList<string> folders, string? current, Func<string, string, string> createFolder)
    {
        _vaultName = vaultName;
        _createFolder = createFolder;
        _folders = folders.ToList();
        Title = title;
        Width = 440;
        Height = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _list = new ListBox { Margin = new Thickness(0, 8, 0, 0) };
        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement { DataContext: Entry }) Accept();
        };
        var ok = new Button { Content = "Move here", IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton"), Padding = new Thickness(14, 4, 14, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)Application.Current.FindResource("DialogButton") };
        var newFolder = new Button { Content = "New Folder\u2026", Padding = new Thickness(10, 3, 10, 3) };
        ok.Click += (_, _) => Accept();
        newFolder.Click += (_, _) => MakeFolder();

        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        DockPanel.SetDock(newFolder, Dock.Left);
        buttons.Children.Add(newFolder);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(ok);
        right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);

        var panel = new DockPanel { Margin = new Thickness(16) };
        var prompt = new TextBlock { Text = "Choose a folder:", TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(prompt, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(prompt);
        panel.Children.Add(buttons);
        panel.Children.Add(_list);
        Content = panel;
        Fill(current ?? "");
    }

    public string? Folder { get; private set; }

    private void Fill(string select)
    {
        var entries = new List<Entry> { new Entry("", "\uD83D\uDDC4  " + _vaultName + "  (top of the vault)") };
        entries.AddRange(_folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f =>
        {
            var depth = f.Count(c => c == '/') + 1;
            var name = f.Substring(f.LastIndexOf('/') + 1);
            return new Entry(f, new string(' ', depth * 5) + "\uD83D\uDCC1  " + name);
        }));
        _list.ItemsSource = entries;
        _list.SelectedItem = entries.FirstOrDefault(e => string.Equals(e.Path, select, StringComparison.OrdinalIgnoreCase)) ?? entries[0];
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void MakeFolder()
    {
        var parent = (_list.SelectedItem as Entry)?.Path ?? "";
        var prompt = new TextPromptWindow("New Folder", $"New folder in {(parent.Length == 0 ? _vaultName : parent)}:", "New Folder",
            Jarvis.Core.Vault.VaultService.FolderNameProblem) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        try
        {
            var created = _createFolder(parent, prompt.Value);
            _folders.Add(created);
            Fill(created);
        }
        catch (PdmException ex)
        {
            MessageBox.Show(this, ex.Message, "New Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Accept()
    {
        if (_list.SelectedItem is not Entry entry) return;
        Folder = entry.Path;
        DialogResult = true;
    }
}
