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
/// </summary>
public partial class InstallWindow : Window
{
    private readonly InstallLayout _layout;
    private readonly string _sourceExe;
    private readonly bool _alreadyInstalled;
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
            SkipButton.Content = "Run this copy anyway";
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
        Closing += (_, e) => e.Cancel = _busy;
    }

    /// <summary>The installed copy was started; this one should exit.</summary>
    public bool StartedInstalledCopy { get; private set; }

    private enum Step { Pending, Working, Done, Problem }

    private static void SetStep(TextBlock block, Step step, string text)
    {
        var mark = step switch
        {
            Step.Working => "\u2192 ",
            Step.Done => "\u2713 ",
            Step.Problem => "! ",
            _ => "\u25CB ",
        };
        block.Text = mark + text;
        block.FontWeight = step == Step.Working ? FontWeights.SemiBold : FontWeights.Normal;
        block.Foreground = step == Step.Problem ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.Black;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        DialogResult = false;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_alreadyInstalled)
        {
            StartInstalled();
            return;
        }

        SetBusy(true);
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

        SetBusy(false);
        StartInstalled();
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
            StartedInstalledCopy = true;
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
        InstallButton.IsEnabled = SkipButton.IsEnabled = !busy;
    }
}
