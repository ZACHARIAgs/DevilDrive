using System;
using System.Windows;
using System.Windows.Threading;
using Jarvis.Client.Services;
using Jarvis.Client.Views;
using Jarvis.Core.Config;
using Jarvis.Core.Git;
using Jarvis.Core.Install;

namespace Jarvis.Client;

public partial class App : Application
{
    /// <summary>
    /// Normal start opens the main window (or, run from outside the install folder, offers to install). "--vaults" opens the
    /// Vaults window. "--register-explorer" and "--explorer ..." come from older versions (Setup, File Explorer's old menu):
    /// they only remove that old File Explorer integration.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        ModernWindows.Apply(this);

        if (args.Length >= 1 && args[0] == "--register-explorer")
        {
            ExplorerCleanup.RemoveOldIntegration();
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

        // Clicked in File Explorer's old Jarvis menu (still there until this cleans it up): just open Jarvis.
        if (args.Length >= 1 && args[0] == "--explorer") args = Array.Empty<string>();

        // Run from Downloads (or anywhere but the install folder): install, like Setup.cmd. This copy then quits.
        if (args.Length == 0 && OfferInstall())
        {
            Shutdown();
            return;
        }

        // First run (no vault on this PC yet): Git, GitHub sign-in, then find or create a vault.
        var settings = UserSettings.Load();
        // "--welcome" shows it anyway (developer check of the first-run screen).
        if (GitHubSignIn.NeedsWelcome(settings) || (args.Length >= 1 && args[0] == "--welcome"))
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

        // "--vaults" comes from the SolidWorks pane's "Add or download a vault".
        var window = new MainWindow { ShowVaultsOnLoad = args.Length >= 1 && args[0] == "--vaults" };
        MainWindow = window;
        window.Show();
        ExplorerCleanup.RemoveOldIntegration();
        AppUpdater.CleanUpAfterUpdate();
        await AppUpdater.CheckAsync(window);
    }

    /// <summary>Shows the Install window when this exe isn't the installed copy. True if it did (this copy should quit).</summary>
    private bool OfferInstall()
    {
        var exe = Environment.ProcessPath;
        var layout = InstallLayout.ForThisUser();
        if (exe == null || AppInstaller.IsDevelopmentBuild(exe) || AppInstaller.IsInstalled(exe, layout)) return false;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        new InstallWindow(layout, exe).ShowDialog();
        return true;
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
