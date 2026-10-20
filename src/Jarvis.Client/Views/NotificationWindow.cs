using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jarvis.Client.Views;

/// <summary>Small borderless message in the corner of the screen, used by the File Explorer commands.</summary>
public sealed class NotificationWindow : Window
{
    private readonly TextBlock _text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)) };
    private readonly DispatcherTimer _closeTimer = new DispatcherTimer();

    public NotificationWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        Background = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE4, 0xE9));
        BorderThickness = new Thickness(1);

        var title = new TextBlock { Text = "Jarvis", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), Margin = new Thickness(0, 0, 0, 4) };
        var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
        panel.Children.Add(title);
        panel.Children.Add(_text);
        Content = panel;

        _closeTimer.Tick += (_, _) => Close();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
        SizeChanged += (_, _) => Top = SystemParameters.WorkArea.Bottom - ActualHeight - 16;
        MouseDown += (_, _) => Close();
    }

    /// <summary>Shows a message; with a timeout it closes by itself.</summary>
    public void Say(string message, TimeSpan? closeAfter = null)
    {
        _text.Text = message;
        if (!IsVisible) Show();
        _closeTimer.Stop();
        if (closeAfter != null)
        {
            _closeTimer.Interval = closeAfter.Value;
            _closeTimer.Start();
        }
    }
}
