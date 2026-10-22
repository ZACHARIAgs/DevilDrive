using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jarvis.Client.Services;
using Jarvis.Core.Git;
using Jarvis.Core.Install;
using Jarvis.Core.Updates;

namespace Jarvis.Client.Views;

/// <summary>
/// Shown when Jarvis.exe runs from outside the install folder (e.g. straight from Downloads): copies
/// itself to %LocalAppData%\Programs\Jarvis, installs Git if it's missing, adds shortcuts and the File
/// Explorer menu, then starts the installed copy. What Setup.cmd does, without the console.
/// There's no "run without installing": a copy outside the install folder doesn't update itself, has no
/// shortcuts or File Explorer menu, and the SolidWorks add-in can't find it. Closing the window quits.
/// </summary>
public partial class InstallWindow : Window
{
    private readonly InstallLayout _layout;
    private readonly string _sourceExe;
    private readonly bool _alreadyInstalled;
    private bool _finished;
    private bool _busy;

    public InstallWindow(InstallLayout layout, string sourceExe)
    {
        InitializeComponent();
        _layout = layout;
        _sourceExe = sourceExe;

        var current = Updater.CurrentVersion(Assembly.GetExecutingAssembly());
        var installed = AppInstaller.InstalledVersion(layout);
        _alreadyInstalled = installed != null && installed >= current;
        if (_alreadyInstalled)
        {
            IntroText.Text = $"Jarvis {installed!.ToString(3)} is already installed on this PC, so you don't need this copy. " +
                             "Open the installed one (it's also on your Desktop and in the Start menu). You can delete this download.";
            StepsPanel.Visibility = Visibility.Collapsed;
            InstallButton.Content = "Open Jarvis";
        }
        else if (installed != null)
        {
            IntroText.Text = $"Jarvis {installed.ToString(3)} is installed on this PC. Update it to {current.ToString(3)}? Your vaults and settings stay as they are.";
            InstallButton.Content = "Update Jarvis";
        }

        SetStep(CopyStep, Step.Pending, $"Copy Jarvis to {layout.InstallFolder}");
        SetStep(GitStep, Step.Pending, "Install Git and Git LFS, if they're missing (Windows may ask for permission)");
        SetStep(ShortcutStep, Step.Pending, "Add Jarvis to the Desktop and the Start menu");
        SetStep(ExplorerStep, Step.Pending, "Add Jarvis to File Explorer's right-click menu and 3D preview");
        // Offer the add-in when SolidWorks is here and the add-in is missing, or older than this Jarvis (it updates along with it).
        var addin = SolidWorksAddinSetup.InstalledVersion;
        var addinUpdate = addin != null && addin < current;
        SetStep(AddinStep, Step.Pending, (addinUpdate ? "Update" : "Install") + " the SolidWorks add-in (Windows asks for permission)");
        if (!_alreadyInstalled && !layout.IsTest && (addin == null || addinUpdate) && SolidWorksAddinSetup.IsSolidWorksInstalled())
        {
            if (addinUpdate)
            {
                AddinBox.Content = $"Also update the SolidWorks add-in ({addin!.ToString(3)} to {current.ToString(3)})";
                AddinNote.Text = "So the add-in matches this version of Jarvis. SolidWorks must be closed, and Windows asks for permission once.";
            }
            AddinChoice.Visibility = Visibility.Visible;
            OnAddinChoice(this, new RoutedEventArgs());
        }
        Closing += (_, e) => e.Cancel = _busy;
    }

    private bool WantsAddin => AddinChoice.Visibility == Visibility.Visible && AddinBox.IsChecked == true;

    private void OnAddinChoice(object sender, RoutedEventArgs e) =>
        AddinStep.Visibility = WantsAddin ? Visibility.Visible : Visibility.Collapsed;

    private enum Step { Pending, Working, Done, Problem }

    private static void SetStep(TextBlock block, Step step, string text)
    {
        var mark = step switch
        {
            Step.Working => "\u2192  ",
            Step.Done => "\u2713  ",
            Step.Problem => "!  ",
            _ => "\u2022  ", // a plain bullet: what will happen, not something to pick
        };
        block.Text = mark + text;
        block.FontWeight = step == Step.Working ? FontWeights.SemiBold : FontWeights.Normal;
        block.Foreground = step switch
        {
            Step.Problem => System.Windows.Media.Brushes.Firebrick,
            Step.Done => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x29, 0x37)),
        };
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_alreadyInstalled || _finished)
        {
            StartInstalled();
            return;
        }

        SetBusy(true);
        AddinBox.IsEnabled = false;
        StatusText.Text = "";
        try
        {
            SetStep(CopyStep, Step.Working, $"Copying Jarvis to {_layout.InstallFolder}\u2026");
            await Task.Run(() => AppInstaller.CopyApp(_sourceExe, _layout));
            SetStep(CopyStep, Step.Done, $"Copied to {_layout.InstallFolder}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStep(CopyStep, Step.Problem, "Jarvis couldn't be copied: " + ex.Message);
            StatusText.Text = "If Jarvis is open, close it (also check the system tray), then click Install Jarvis again.";
            SetBusy(false);
            return;
        }

        await InstallGitAsync();

        try
        {
            SetStep(ShortcutStep, Step.Working, "Adding shortcuts\u2026");
            AppInstaller.CreateShortcuts(_layout);
            SetStep(ShortcutStep, Step.Done, "Added to the Desktop and the Start menu");
        }
        catch (Exception ex)
        {
            SetStep(ShortcutStep, Step.Problem, "The shortcuts couldn't be added (" + ex.Message + "). Start Jarvis from " + _layout.ExePath);
        }

        if (_layout.IsTest)
        {
            SetStep(ExplorerStep, Step.Done, $"Skipped: trial install ({InstallLayout.TestRootVariable} is set)");
        }
        else
        {
            SetStep(ExplorerStep, Step.Working, "Adding Jarvis to File Explorer\u2026");
            var registered = await RunInstalledAsync("--register-explorer");
            SetStep(ExplorerStep, registered ? Step.Done : Step.Problem, registered
                ? "Added to File Explorer (on Windows 11, right-click and choose Show more options)"
                : "File Explorer's menu wasn't added; Jarvis adds it the next time it starts");
        }

        var addinOk = !WantsAddin || await InstallAddinAsync();

        SetBusy(false);
        if (addinOk)
        {
            StartInstalled();
            return;
        }
        // Leave the problem on screen; Jarvis is installed either way.
        _finished = true;
        InstallButton.Content = "Open Jarvis";
        StatusText.Text = "Jarvis is installed. Add the SolidWorks add-in later in Jarvis's Settings.";
    }

    /// <summary>The add-in from the latest release (SolidWorks has to be closed). False if it wasn't installed.</summary>
    private async Task<bool> InstallAddinAsync()
    {
        while (SolidWorksAddinSetup.IsSolidWorksRunning)
        {
            SetStep(AddinStep, Step.Working, "Waiting for SolidWorks to close\u2026");
            var answer = MessageBox.Show(this,
                "To install the add-in, close SolidWorks (save your work first), then click OK.\n\nCancel skips the add-in; you can add it later in Jarvis's Settings.",
                "Install the SolidWorks add-in", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (answer != MessageBoxResult.OK)
            {
                SetStep(AddinStep, Step.Done, "SolidWorks add-in skipped (add it later in Jarvis's Settings)");
                return true;
            }
        }
        SetStep(AddinStep, Step.Working, "Installing the SolidWorks add-in. Click Yes when Windows asks for permission\u2026");
        try
        {
            await SolidWorksAddinSetup.InstallLatestAsync();
            SetStep(AddinStep, Step.Done, "SolidWorks add-in installed (Jarvis is in SolidWorks' Task Pane)");
            return true;
        }
        catch (Jarvis.Core.Errors.PdmException ex)
        {
            SetStep(AddinStep, Step.Problem, "The SolidWorks add-in wasn't installed: " + ex.Message);
            return false;
        }
    }

    private async Task InstallGitAsync()
    {
        SetStep(GitStep, Step.Working, "Checking for Git\u2026");
        var tools = await GitHubSignIn.CheckToolsAsync();
        if (!tools.Ready)
        {
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            SetStep(GitStep, Step.Working, tools.GitPath == null ? "Installing Git for Windows (includes Git LFS)\u2026" : "Installing Git LFS\u2026");
            var progress = new Progress<WingetProgress>(p =>
            {
                StatusText.Text = p.Text;
                Progress.IsIndeterminate = p.Percent == null;
                if (p.Percent is { } percent) Progress.Value = percent;
            });
            var instead = await GitSetup.InstallAsync(tools, progress);
            Progress.Visibility = Visibility.Collapsed;
            StatusText.Text = "";
            tools = await GitHubSignIn.CheckToolsAsync();
            if (!tools.Ready)
            {
                SetStep(GitStep, Step.Problem, instead ?? "Git didn't install. Jarvis's welcome screen will help you try again.");
                return;
            }
        }
        SetStep(GitStep, Step.Done, "Ready: " + tools.GitVersion);
    }

    /// <summary>Runs the installed exe with a command and waits (at most a minute). False if it didn't work.</summary>
    private async Task<bool> RunInstalledAsync(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(_layout.ExePath, arguments) { UseShellExecute = false });
            if (process == null) return false;
            var exited = process.WaitForExitAsync();
            return await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMinutes(1))) == exited && process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private void StartInstalled()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_layout.ExePath) { UseShellExecute = true, WorkingDirectory = _layout.InstallFolder });
            DialogResult = true;
        }
        catch (Win32Exception ex)
        {
            StatusText.Text = $"The installed Jarvis didn't start ({ex.Message}). Start it from {_layout.ExePath}.";
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.Wait : null;
        InstallButton.IsEnabled = !busy;
    }
}
