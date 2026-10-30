using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Client.ViewModels;

namespace Jarvis.Client.Views;

/// <summary>Export (like Pack and Go): where to, folder or zip, drawings, and whether to keep the vault's folders.</summary>
internal sealed class ExportWindow : Window
{
    private readonly TextBox _parent;
    private readonly TextBox _name;
    private readonly RadioButton _zip;
    private readonly CheckBox _drawings;
    private readonly CheckBox _keepFolders;
    private readonly TextBlock _problem;

    public ExportWindow(string what, string defaultName)
    {
        Title = "Export";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var dialogButton = (Style)Application.Current.FindResource("DialogButton");
        _parent = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Padding = new Thickness(6, 4, 6, 4) };
        var browse = new Button { Content = "Browse…", Style = dialogButton, Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Export to", InitialDirectory = Directory.Exists(_parent.Text) ? _parent.Text : null };
            if (picker.ShowDialog(this) == true) _parent.Text = picker.FolderName;
        };
        var where = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        where.Children.Add(browse);
        where.Children.Add(_parent);

        _name = new TextBox { Text = defaultName, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 0) };
        var folder = new RadioButton { Content = "A folder", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        _zip = new RadioButton { Content = "A .zip file (to email or upload)" };
        var kind = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        kind.Children.Add(folder);
        kind.Children.Add(_zip);
        _drawings = new CheckBox { Content = "Include the drawings of these files", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        _keepFolders = new CheckBox
        {
            Content = new TextBlock { Text = "Keep the vault's folders (otherwise everything goes in one folder, like Pack and Go)", TextWrapping = TextWrapping.Wrap },
            Margin = new Thickness(0, 6, 0, 0),
        };
        _problem = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

        var ok = new Button { Content = "Export", IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton"), Padding = new Thickness(14, 4, 14, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = dialogButton };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Export {what} and every part and sub-assembly {(what.Contains(" and ") ? "they use" : "it uses")}. The copies' assemblies and drawings " +
                   "are pointed at each other, so they open on any PC, without the vault. Nothing in the vault changes.",
        });
        panel.Children.Add(new TextBlock { Text = "Save to:", Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(where);
        panel.Children.Add(new TextBlock { Text = "Name:", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(_name);
        panel.Children.Add(kind);
        panel.Children.Add(_drawings);
        panel.Children.Add(_keepFolders);
        panel.Children.Add(_problem);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            _name.Focus();
            _name.SelectAll();
        };
    }

    public ExportOptions? Result { get; private set; }

    private void Accept()
    {
        var parent = _parent.Text.Trim();
        var name = _name.Text.Trim();
        var zip = _zip.IsChecked == true;
        if (zip && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4).TrimEnd();
        string? problem = null;
        if (!Directory.Exists(parent)) problem = "Pick a folder that exists to save the export in.";
        else if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) problem = "Type a name without any of these: \\ / : * ? \" < > |";
        var destination = problem == null ? Path.Combine(parent, zip ? name + ".zip" : name) : "";
        if (problem == null && (File.Exists(destination) || (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).GetEnumerator().MoveNext())))
        {
            problem = $"There's already {(zip ? "a file" : "a folder with files")} named {Path.GetFileName(destination)} there. Type another name.";
        }
        if (problem != null)
        {
            _problem.Text = problem;
            return;
        }
        Result = new ExportOptions(destination, zip, _keepFolders.IsChecked == true, _drawings.IsChecked == true);
        DialogResult = true;
    }
}
