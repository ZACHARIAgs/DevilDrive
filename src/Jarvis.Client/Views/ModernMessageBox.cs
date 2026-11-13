using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Jarvis.Client.Views;

/// <summary>
/// The app's own message window, in place of Windows' MessageBox: the same questions and answers, but in the app's
/// look (light or dark) and with text that can be selected and copied. Called like MessageBox.Show.
/// </summary>
internal static class ModernMessageBox
{
    public static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        // Closing the window with X or Esc answers like the least committing button.
        var result = buttons is MessageBoxButton.OK ? MessageBoxResult.OK : buttons is MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
        var window = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 640,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        if (owner is { IsLoaded: true }) window.Owner = owner;

        var (glyph, brush) = image switch
        {
            MessageBoxImage.Warning => ("", "WarningTextBrush"),
            MessageBoxImage.Error => ("", "DangerBrush"),
            MessageBoxImage.Question => ("", "AccentBrush"),
            MessageBoxImage.Information => ("", "AccentBrush"),
            _ => ("", "AccentBrush"),
        };
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 26,
            Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
            Visibility = glyph.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);

        // A read-only text box: the message can be selected and copied, e.g. to ask a teammate about it.
        var text = new TextBox
        {
            Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), MinHeight = 0, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460, IsTabStop = false,
        };
        var body = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        body.Children.Add(text);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        void Add(string label, MessageBoxResult value, bool isCancel)
        {
            var button = new Button { Content = label, Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = isCancel };
            button.Click += (_, _) =>
            {
                result = value;
                window.Close();
            };
            row.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                Add("OK", MessageBoxResult.OK, false);
                Add("Cancel", MessageBoxResult.Cancel, true);
                break;
            case MessageBoxButton.YesNo:
                Add("Yes", MessageBoxResult.Yes, false);
                Add("No", MessageBoxResult.No, true);
                break;
            case MessageBoxButton.YesNoCancel:
                Add("Yes", MessageBoxResult.Yes, false);
                Add("No", MessageBoxResult.No, false);
                Add("Cancel", MessageBoxResult.Cancel, true);
                break;
            default:
                Add("OK", MessageBoxResult.OK, true);
                break;
        }
        // The blue (default) button: the one asked for, else the first.
        var all = row.Children.OfType<Button>().ToList();
        var wanted = defaultResult == MessageBoxResult.Cancel ? "Cancel" : defaultResult == MessageBoxResult.No ? "No" : null;
        var preferred = all.FirstOrDefault(b => (string)b.Content == wanted) ?? all[0];
        preferred.IsDefault = true;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(body);
        panel.Children.Add(row);
        window.Content = panel;
        window.Loaded += (_, _) => preferred.Focus();
        window.ShowDialog();
        return result;
    }
}
