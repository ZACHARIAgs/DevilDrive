using System;
using System.Windows;
using System.Windows.Threading;
using CapstonePdm.Client.Services;
using CapstonePdm.Client.Views;
using CapstonePdm.Core.Config;

namespace CapstonePdm.Client;

public partial class App : Application
{
    /// <summary>
    /// Normal start opens the main window. "--explorer &lt;command&gt; &lt;path&gt;" comes from File Explorer's
    /// right-click menu; "--register-explorer" (run by Setup) adds that menu.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Length >= 1 && args[0] == "--register-explorer")
        {
            UpdateExplorerMenu(UserSettings.Load(), force: true);
            Shutdown();
            return;
        }

        if (args.Length >= 3 && args[0] == "--explorer")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await ExplorerCommandHost.RunAsync(args[1], args[2]);
            // "Open"/"Show in Capstone PDM" leave the main window open; everything else is done.
            if (MainWindow == null || !MainWindow.IsVisible) Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        UpdateExplorerMenu(UserSettings.Load());
        AppUpdater.CleanUpAfterUpdate();
        await AppUpdater.CheckAsync(window);
    }

    /// <summary>Keeps the right-click menu pointing at this copy and the current vault folder (or removes it).</summary>
    public static void UpdateExplorerMenu(UserSettings settings, bool force = false)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            if (!settings.ExplorerMenu)
            {
                ExplorerIntegration.Unregister();
            }
            else if (force || ExplorerIntegration.IsInstalledCopy(exe))
            {
                ExplorerIntegration.Register(exe, settings.Vaults);
            }
        }
        catch (Exception)
        {
            // The menu is a convenience; never block the app over it.
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Something unexpected went wrong:\n\n" + e.Exception.Message +
            "\n\nYour files are safe. If this keeps happening, send a screenshot of this message to whoever maintains Capstone PDM.",
            "Capstone PDM", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
