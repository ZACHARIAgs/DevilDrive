using System;
using System.Windows;
using System.Windows.Threading;
using Jarvis.Client.Services;
using Jarvis.Client.Views;
using Jarvis.Core.Config;
using Jarvis.Core.Git;

namespace Jarvis.Client;

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
        ModernWindows.Apply(this);

        if (args.Length >= 1 && args[0] == "--register-explorer")
        {
            UpdateExplorerMenu(UserSettings.Load(), force: true);
            Shutdown();
            return;
        }

        if (args.Length >= 3 && args[0] == "--render-preview")
        {
            // Developer check of how 3D previews look: --render-preview in.stl out.png
            StlLoader.RenderToPng(args[1], args[2]);
            Shutdown();
            return;
        }

        if (args.Length >= 3 && args[0] == "--explorer")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await ExplorerCommandHost.RunAsync(args[1], args[2]);
            // "Open"/"Show in Jarvis" leave the main window open; everything else is done.
            if (MainWindow == null || !MainWindow.IsVisible) Shutdown();
            return;
        }

        // First run (no vault on this PC yet): Git, GitHub sign-in, then find or create a vault.
        var settings = UserSettings.Load();
        if (GitHubSignIn.NeedsWelcome(settings))
        {
            var shutdownMode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing the welcome screen isn't quitting
            var welcome = new WelcomeWindow();
            if (welcome.ShowDialog() == true && welcome.OpenFolder != null)
            {
                settings = UserSettings.Load(); // the Vaults window saved the list
                settings.VaultPath = welcome.OpenFolder;
                settings.AddVault(welcome.OpenFolder);
                settings.Save();
            }
            ShutdownMode = shutdownMode;
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
            ExplorerPreview.WriteVaultList(settings.Vaults);
            UpdatePreviewPane(settings, force);
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

    /// <summary>Installs or removes the 3D preview pane add-on to match Settings (installed copy only, like the menu).</summary>
    private static void UpdatePreviewPane(UserSettings settings, bool force)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!settings.PreviewPane) ExplorerPreview.Unregister();
            else if (exe != null && (force || ExplorerIntegration.IsInstalledCopy(exe))) ExplorerPreview.Register();
        }
        catch (Exception)
        {
            // A convenience; never block the app over it.
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Something unexpected went wrong:\n\n" + e.Exception.Message +
            "\n\nYour files are safe. If this keeps happening, send a screenshot of this message to whoever maintains Jarvis.",
            "Jarvis", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
