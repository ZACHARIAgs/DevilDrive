using System.Windows;
using System.Windows.Threading;

namespace CapstonePdm.Client;

public partial class App : Application
{
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Something unexpected went wrong:\n\n" + e.Exception.Message +
            "\n\nYour files are safe. If this keeps happening, send a screenshot of this message to whoever maintains Capstone PDM.",
            "Capstone PDM", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
