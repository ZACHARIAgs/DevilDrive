using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jarvis.Core.Vault;

namespace Jarvis.Client.Views;

/// <summary>
/// Add a McMaster-Carr part: type its part number, Jarvis opens the part's page, the user clicks McMaster's
/// Download (SOLIDWORKS), and this window waits for the file to land in Downloads. <see cref="DownloadedFile"/> is it.
/// </summary>
internal sealed class McMasterWindow : Window
{
    private readonly TextBox _partNumber;
    private readonly TextBlock _status;
    private readonly Button _get;
    private CancellationTokenSource? _waiting;

    public McMasterWindow()
    {
        Title = "Add a McMaster-Carr part";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _partNumber = new TextBox { Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 4, 6, 4), FontSize = 15 };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = System.Windows.Media.Brushes.DimGray };
        _get = new Button { Content = "Get from McMaster", IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton"), Padding = new Thickness(14, 4, 14, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)Application.Current.FindResource("DialogButton") };
        _get.Click += async (_, _) => await GetAsync();
        Closing += (_, _) => _waiting?.Cancel();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_get);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "McMaster-Carr part number", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = $"For example 91251A540. Jarvis opens the part on McMaster's site; click Download there and choose SOLIDWORKS. " +
                   $"Jarvis puts the file in {McMaster.Folder.Replace('/', '\\')} in the vault.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 2, 0, 0),
        });
        panel.Children.Add(_partNumber);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _partNumber.Focus();
    }

    /// <summary>The part number that was fetched.</summary>
    public string PartNumber { get; private set; } = "";

    /// <summary>The file McMaster's download put in the Downloads folder.</summary>
    public string? DownloadedFile { get; private set; }

    private async Task GetAsync()
    {
        var partNumber = McMaster.Normalize(_partNumber.Text);
        if (partNumber == null)
        {
            _status.Text = "That doesn't look like a McMaster part number (digits, a letter or two, digits, e.g. 91251A540).";
            return;
        }
        PartNumber = partNumber;
        var downloads = DownloadsFolder();
        var since = DateTime.UtcNow;
        try
        {
            Process.Start(new ProcessStartInfo(McMaster.PageUrl(partNumber)) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            _status.Text = $"Open {McMaster.PageUrl(partNumber)} in your browser.";
        }

        _get.IsEnabled = _partNumber.IsEnabled = false;
        Cursor = Cursors.AppStarting;
        _status.Text = $"On McMaster's page for {partNumber}, click Download (under the picture), choose SOLIDWORKS, and save it. " +
                       $"Jarvis is watching {downloads} and takes it from there\u2026";
        _waiting = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            DownloadedFile = await Task.Run(() => WaitForDownload(downloads, partNumber, since, _waiting.Token));
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            if (!IsVisible) return;
            _status.Text = "Nothing arrived in Downloads. Click Get from McMaster to try again.";
            _get.IsEnabled = _partNumber.IsEnabled = true;
            Cursor = null;
        }
    }

    /// <summary>Waits until the downloaded file is there and finished (the browser has let go of it).</summary>
    private static string WaitForDownload(string folder, string partNumber, DateTime sinceUtc, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var files = Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles().Select(f => (f.FullName, f.LastWriteTimeUtc))
                : Enumerable.Empty<(string, DateTime)>();
            if (McMaster.FindDownload(files, partNumber, sinceUtc) is { } found && IsFinished(found)) return found;
            Thread.Sleep(1000);
        }
    }

    private static bool IsFinished(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
        }
        catch (IOException)
        {
            return false; // still being written
        }
    }

    /// <summary>The user's Downloads folder (wherever they moved it).</summary>
    private static string DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0 && !string.IsNullOrEmpty(path)) return path;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
