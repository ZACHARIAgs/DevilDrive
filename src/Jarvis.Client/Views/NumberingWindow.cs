using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Core.Vault;

namespace Jarvis.Client.Views;

/// <summary>
/// Admin: the vault's part-numbering convention. Prefix, digits and groups (e.g. 200 Turret, 400 Bending
/// fixture); new parts checked in from SolidWorks then get the next free number in the group they pick.
/// </summary>
internal sealed class NumberingWindow : Window
{
    private readonly TextBox _prefix = new TextBox { Width = 90 };
    private readonly TextBox _separator = new TextBox { Width = 40 };
    private readonly ComboBox _digits = new ComboBox { Width = 60 };
    private readonly TextBox _nameSeparator = new TextBox { Width = 40 };
    private readonly StackPanel _groups = new StackPanel();
    private readonly TextBlock _example = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _problem = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public NumberingWindow(NumberingScheme? current)
    {
        Title = "Part numbering (admin)";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var scheme = current ?? new NumberingScheme { Prefix = "", Groups = new List<NumberGroup> { new NumberGroup { Number = 100, Name = "" } } };
        _prefix.Text = scheme.Prefix;
        _separator.Text = scheme.Separator;
        _nameSeparator.Text = scheme.NameSeparator;
        foreach (var d in new[] { 2, 3, 4, 5, 6 }) _digits.Items.Add(d);
        _digits.SelectedItem = scheme.Digits;
        foreach (var group in scheme.Groups.OrderBy(g => g.Number)) AddGroupRow(group.Number.ToString(CultureInfo.InvariantCulture), group.Name);
        foreach (var box in new Control[] { _prefix, _separator, _nameSeparator }) ((TextBox)box).TextChanged += (_, _) => UpdateExample();
        _digits.SelectionChanged += (_, _) => UpdateExample();

        var settings = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Field(string label, Control control)
        {
            settings.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            control.Margin = new Thickness(0, 0, 16, 4);
            settings.Children.Add(control);
        }
        Field("Prefix", _prefix);
        Field("then", _separator);
        Field("Digits", _digits);
        Field("before the name", _nameSeparator);

        var addGroup = new Button { Content = "Add a group", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 4, 0, 0) };
        addGroup.Click += (_, _) =>
        {
            var last = Groups().Select(g => g.Number).DefaultIfEmpty(0).Max();
            AddGroupRow((last + (Digit() <= 2 ? 10 : 100)).ToString(CultureInfo.InvariantCulture), "");
            UpdateExample();
        };

        var save = new Button { Content = "Save for everyone", IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton"), Padding = new Thickness(14, 4, 14, 4) };
        var remove = new Button { Content = "Turn off numbering", Style = (Style)Application.Current.FindResource("DialogButton"), Visibility = current == null ? Visibility.Collapsed : Visibility.Visible };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)Application.Current.FindResource("DialogButton") };
        save.Click += (_, _) =>
        {
            var built = Build();
            if (built.Problem() is { } problem)
            {
                _problem.Text = problem;
                return;
            }
            Result = built;
            DialogResult = true;
        };
        remove.Click += (_, _) =>
        {
            Removed = true;
            DialogResult = true;
        };

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(remove, Dock.Left);
        buttons.Children.Add(remove);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(save);
        right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "When someone checks in a new part from SolidWorks, Jarvis asks which group it belongs to, gives it the next free number " +
                   "(written into its PartNumber property) and offers to rename the file to match. Everyone in the vault gets this setting.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
        });
        panel.Children.Add(settings);
        panel.Children.Add(new TextBlock { Text = "Groups (number and name)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        panel.Children.Add(_groups);
        panel.Children.Add(addGroup);
        panel.Children.Add(_example);
        panel.Children.Add(_problem);
        panel.Children.Add(buttons);
        Content = panel;
        UpdateExample();
    }

    /// <summary>The convention to save (when not <see cref="Removed"/>).</summary>
    public NumberingScheme? Result { get; private set; }

    /// <summary>The admin turned numbering off.</summary>
    public bool Removed { get; private set; }

    private int Digit() => _digits.SelectedItem is int d ? d : 3;

    private void AddGroupRow(string number, string name)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var numberBox = new TextBox { Text = number, Width = 70 };
        var nameBox = new TextBox { Text = name };
        var delete = new Button { Content = "Remove", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        delete.Click += (_, _) =>
        {
            _groups.Children.Remove(row);
            UpdateExample();
        };
        numberBox.TextChanged += (_, _) => UpdateExample();
        nameBox.TextChanged += (_, _) => UpdateExample();
        DockPanel.SetDock(numberBox, Dock.Left);
        DockPanel.SetDock(delete, Dock.Right);
        nameBox.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(numberBox);
        row.Children.Add(delete);
        row.Children.Add(nameBox);
        row.Tag = (numberBox, nameBox);
        _groups.Children.Add(row);
    }

    private List<NumberGroup> Groups() =>
        _groups.Children.OfType<DockPanel>()
            .Select(r => r.Tag is (TextBox number, TextBox name)
                ? new NumberGroup { Number = int.TryParse(number.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1, Name = name.Text.Trim() }
                : null)
            .Where(g => g != null).Select(g => g!).ToList();

    private NumberingScheme Build() => new NumberingScheme
    {
        Prefix = _prefix.Text.Trim(),
        Separator = _separator.Text,
        Digits = Digit(),
        NameSeparator = _nameSeparator.Text.Length == 0 ? "_" : _nameSeparator.Text,
        Groups = Groups(),
    };

    private void UpdateExample()
    {
        var scheme = Build();
        _problem.Text = "";
        _example.Text = scheme.Problem() == null
            ? "Example: " + scheme.Example() + "  (groups: " + string.Join(", ", scheme.Groups.OrderBy(g => g.Number).Select(g => $"{scheme.Format(scheme.RangeOf(g).First)}\u2026 {g.Name}")) + ")"
            : "";
    }
}
