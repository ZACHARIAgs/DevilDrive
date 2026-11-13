using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jarvis.Core.Help;
using Jarvis.Core.Vault;

namespace Jarvis.Client.Views;

/// <summary>Help: the common problems and how to fix them, searchable (HelpTopics, shared with the SolidWorks pane).</summary>
internal sealed class HelpWindow : Window
{
    private readonly TextBox _search = new TextBox { Padding = new Thickness(6, 4, 6, 4) };
    private readonly StackPanel _topics = new StackPanel();
    private readonly TextBlock _none = new TextBlock { Text = "Nothing matches. Try fewer words.", Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("MutedTextBrush"), Margin = new Thickness(4, 8, 0, 0), Visibility = Visibility.Collapsed };

    /// <param name="search">Words to search for straight away (e.g. an error's title); ignored if nothing matches them.</param>
    public HelpWindow(string? search = null)
    {
        Title = "DevilDrive Help";
        Width = 640;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        foreach (var topic in HelpTopics.All) _topics.Children.Add(TopicView(topic));
        _search.TextChanged += (_, _) => Filter();

        var backups = new Button { Content = "Open backups folder", Style = (Style)Application.Current.FindResource("DialogButton") };
        backups.Click += (_, _) =>
        {
            Directory.CreateDirectory(FileUtil.BackupRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FileUtil.BackupRoot}\"") { UseShellExecute = true });
        };
        var close = new Button { Content = "Close", IsCancel = true, Style = (Style)Application.Current.FindResource("DialogButton") };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(backups);
        buttons.Children.Add(close);

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "Common problems and how to fix them. Click one to see the steps, or search for words from the message you got.",
            TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("MutedTextBrush"), Margin = new Thickness(0, 0, 0, 8),
        });
        header.Children.Add(_search);
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        list.Children.Add(_topics);
        list.Children.Add(_none);

        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
        Loaded += (_, _) => _search.Focus();
        if (!string.IsNullOrWhiteSpace(search) && HelpTopics.All.Any(t => t.Matches(search))) _search.Text = search;
    }

    private static Expander TopicView(HelpTopic topic)
    {
        var body = new StackPanel { Margin = new Thickness(22, 4, 4, 10) };
        body.Children.Add(new TextBlock { Text = topic.Seen, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        for (var i = 0; i < topic.Steps.Count; i++)
        {
            body.Children.Add(new TextBlock { Text = $"{i + 1}.  {topic.Steps[i]}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        }
        return new Expander
        {
            Header = new TextBlock { Text = topic.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
            Content = body,
            Tag = topic,
            Margin = new Thickness(0, 2, 0, 2),
        };
    }

    private void Filter()
    {
        var query = _search.Text.Trim();
        var shown = 0;
        foreach (var view in _topics.Children.OfType<Expander>())
        {
            var match = ((HelpTopic)view.Tag).Matches(query);
            view.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match) shown++;
        }
        // A search with only a few results opens them.
        foreach (var view in _topics.Children.OfType<Expander>()) view.IsExpanded = query.Length > 0 && shown <= 3 && view.Visibility == Visibility.Visible;
        _none.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
