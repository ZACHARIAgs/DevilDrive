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
/// Shown when DevilDrive.exe runs from outside the install folder (e.g. straight from Downloads): copies
/// itself to %LocalAppData%\Programs\DevilDrive, installs Git if it's missing, adds shortcuts (and the
/// SolidWorks add-in if chosen), then starts the installed copy. What Setup.cmd does, without the console.
/// There's no "run without installing": a copy outside the install folder doesn't update itself, has no
/// shortcuts, and the SolidWorks add-in can't find it. Closing the window quits.
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
            IntroText.Text = $"DevilDrive {Updater.Format(installed)} is already installed on this PC, so you don't need this copy. " +
                             "Open the installed one (it's also on your Desktop and in the Start menu). You can delete this download.";
            StepsPanel.Visibility = Visibility.Collapsed;
            InstallButton.Content = "Open DevilDrive";
        }
        else if (installed != null)
        {
            IntroText.Text = $"DevilDrive {Updater.Format(installed)} is installed on this PC. Update it to {Updater.Format(current)}? Your vaults and settings stay as they are.";
            InstallButton.Content = "Update DevilDrive";
        }

        SetStep(CopyStep, Step.Pending, $"Copy DevilDrive to {layout.InstallFolder}");
        SetStep(GitStep, Step.Pending, "Install Git and Git LFS, if they're missing (Windows may ask for permission)");
        SetStep(ShortcutStep, Step.Pending, "Add DevilDrive to the Desktop and the Start menu");
        // Offer the add-in when SolidWorks is here and the add-in is missing, or older than this DevilDrive (it updates along with it).
        var addin = SolidWorksAddinSetup.InstalledVersion;
        var addinUpdate = addin != null && addin < current;
        SetStep(AddinStep, Step.Pending, (addinUpdate ? "Update" : "Install") + " the SolidWorks add-in (Windows asks for permission)");
        if (!_alreadyInstalled && !layout.IsTest && (addin == null || addinUpdate) && SolidWorksAddinSetup.IsSolidWorksInstalled())
        {
            if (addinUpdate)
            {
                AddinBox.Content = $"Also update the SolidWorks add-in ({Updater.Format(addin)} to {Updater.Format(current)})";
                AddinNote.Text = "So the add-in matches this version of DevilDrive. SolidWorks must be closed, and Windows asks for permission once.";
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
            Step.Problem => ThemeManager.IsDark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71)) : System.Windows.Media.Brushes.Firebrick,
            Step.Done => ThemeManager.IsDark ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D)),
            _ => (System.Windows.Media.Brush?)Application.Current?.TryFindResource("TextBrush") ?? System.Windows.Media.Brushes.Black,
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
            SetStep(CopyStep, Step.Working, $"Copying DevilDrive to {_layout.InstallFolder}\u2026");
            await Task.Run(() => AppInstaller.CopyApp(_sourceExe, _layout));
            SetStep(CopyStep, Step.Done, $"Copied to {_layout.InstallFolder}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStep(CopyStep, Step.Problem, "DevilDrive couldn't be copied: " + ex.Message);
            StatusText.Text = "If DevilDrive is open, close it (also check the system tray), then click Install DevilDrive again.";
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
            SetStep(ShortcutStep, Step.Problem, "The shortcuts couldn't be added (" + ex.Message + "). Start DevilDrive from " + _layout.ExePath);
        }


        var addinOk = !WantsAddin || await InstallAddinAsync();

        SetBusy(false);
        if (addinOk)
        {
            StartInstalled();
            return;
        }
        // Leave the problem on screen; DevilDrive is installed either way.
        _finished = true;
        InstallButton.Content = "Open DevilDrive";
        StatusText.Text = "DevilDrive is installed. Add the SolidWorks add-in later in DevilDrive's Settings.";
    }

    /// <summary>The add-in from the latest release (SolidWorks has to be closed). False if it wasn't installed.</summary>
    private async Task<bool> InstallAddinAsync()
    {
        while (SolidWorksAddinSetup.IsSolidWorksRunning)
        {
            if (SolidWorksAddinSetup.OnlyHiddenSolidWorksRunning)
            {
                // A SolidWorks with no window (left by a crash or another program): the user can't close it themselves.
                if (MessageBox.Show(this, "SolidWorks is still running in the background, without a window (left over from a crash or started by another program).\n\n" +
                                          "End it now, so the add-in can be installed? Nothing you can see is open in it.",
                        "Install the SolidWorks add-in", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    SetStep(AddinStep, Step.Done, "SolidWorks add-in skipped (add it later in DevilDrive's Settings)");
                    return true;
                }
                SolidWorksAddinSetup.EndHiddenSolidWorks();
                await Task.Delay(2000);
                continue;
            }
            SetStep(AddinStep, Step.Working, "Waiting for SolidWorks to close\u2026");
            var answer = MessageBox.Show(this,
                "To install the add-in, close SolidWorks (save your work first), then click OK.\n\nCancel skips the add-in; you can add it later in DevilDrive's Settings.",
                "Install the SolidWorks add-in", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (answer != MessageBoxResult.OK)
            {
                SetStep(AddinStep, Step.Done, "SolidWorks add-in skipped (add it later in DevilDrive's Settings)");
                return true;
            }
        }
        SetStep(AddinStep, Step.Working, "Installing the SolidWorks add-in. Click Yes when Windows asks for permission\u2026");
        try
        {
            await SolidWorksAddinSetup.InstallLatestAsync(_sourceExe);
            SetStep(AddinStep, Step.Done, "SolidWorks add-in installed (DevilDrive is in SolidWorks' Task Pane)");
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
                SetStep(GitStep, Step.Problem, instead ?? "Git didn't install. DevilDrive's welcome screen will help you try again.");
                return;
            }
        }
        SetStep(GitStep, Step.Done, "Ready: " + tools.GitVersion);
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
            StatusText.Text = $"The installed DevilDrive didn't start ({ex.Message}). Start it from {_layout.ExePath}.";
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.Wait : null;
        InstallButton.IsEnabled = !busy;
    }
}
