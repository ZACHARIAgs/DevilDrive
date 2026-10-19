using System.Windows;
using CapstonePdm.Core.Errors;

namespace CapstonePdm.Client.Views;

public partial class ErrorWindow : Window
{
    private readonly PdmException _error;

    public ErrorWindow(PdmException error)
    {
        InitializeComponent();
        _error = error;
        Title = "Capstone PDM — " + error.Title;
        TitleText.Text = error.Title;
        MessageText.Text = error.Message;
        DetailsBox.Text = error.Detail ?? "";
        DetailsExpander.Visibility = string.IsNullOrWhiteSpace(error.Detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCopy(object sender, RoutedEventArgs e) =>
        Clipboard.SetText($"{_error.Title}\n{_error.Message}\n\n{_error.Detail}".TrimEnd());

    private void OnOk(object sender, RoutedEventArgs e) => Close();
}
