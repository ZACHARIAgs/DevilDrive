using System.Windows;
using Jarvis.Core.Errors;

namespace Jarvis.Client.Views;

public partial class ErrorWindow : Window
{
    private readonly PdmException _error;

    /// <summary>True when the user clicked the offered fix instead of OK.</summary>
    public bool FixChosen { get; private set; }

    /// <param name="fixText">A one-click way out of this error (e.g. "Keep copies, get the vault's"), shown as the blue button.</param>
    public ErrorWindow(PdmException error, string? fixText = null, string? fixToolTip = null)
    {
        InitializeComponent();
        if (fixText != null)
        {
            FixButton.Content = fixText;
            FixButton.ToolTip = fixToolTip;
            FixButton.Visibility = Visibility.Visible;
        }
        _error = error;
        Title = "DevilDrive — " + error.Title;
        TitleText.Text = error.Title;
        MessageText.Text = error.Message;
        DetailsBox.Text = error.Detail ?? "";
        DetailsExpander.Visibility = string.IsNullOrWhiteSpace(error.Detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Help, already searched for this kind of error (all topics if none fits).</summary>
    private void OnHelp(object sender, RoutedEventArgs e) => new HelpWindow(_error.Title) { Owner = this }.ShowDialog();

    private void OnCopy(object sender, RoutedEventArgs e) =>
        Clipboard.SetText($"{_error.Title}\n{_error.Message}\n\n{_error.Detail}".TrimEnd());

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnFix(object sender, RoutedEventArgs e)
    {
        FixChosen = true;
        Close();
    }
}
