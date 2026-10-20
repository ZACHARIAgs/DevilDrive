using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CapstonePdm.Client.Views;
using CapstonePdm.Core.Config;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Vault;

namespace CapstonePdm.Client.Services;

/// <summary>
/// Runs a File Explorer right-click command ("CapstonePDM.exe --explorer checkout &lt;file&gt;") without
/// opening the main window. Explorer starts one process per selected file, so the first process
/// collects the others' paths over a named pipe and runs the command once for all of them.
/// </summary>
internal static class ExplorerCommandHost
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan MaxCollectTime = TimeSpan.FromSeconds(4);

    public static async Task RunAsync(string command, string path)
    {
        var channel = $"CapstonePDM.Explorer.{command}.{Environment.UserName}";
        using var mutex = new Mutex(true, channel, out var first);
        if (!first && await SendToFirstAsync(channel, path)) return;

        var paths = new List<string> { path };
        if (first) await CollectAsync(channel, paths);
        await ExecuteAsync(command, paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static async Task<bool> SendToFirstAsync(string channel, string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", channel, PipeDirection.Out);
                await client.ConnectAsync(200);
                using var writer = new StreamWriter(client);
                await writer.WriteLineAsync(path);
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException)
            {
                await Task.Delay(100);
            }
        }
        return false; // the first process already finished collecting; run on our own
    }

    private static async Task CollectAsync(string channel, List<string> paths)
    {
        var stopAt = DateTime.Now + MaxCollectTime;
        var quietUntil = DateTime.Now + QuietPeriod;
        while (DateTime.Now < quietUntil && DateTime.Now < stopAt)
        {
            using var server = new NamedPipeServerStream(channel, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var wait = new CancellationTokenSource(quietUntil - DateTime.Now);
            try
            {
                await server.WaitForConnectionAsync(wait.Token);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line)) paths.Add(line!.Trim());
                quietUntil = DateTime.Now + QuietPeriod;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task ExecuteAsync(string command, List<string> paths)
    {
        var note = new NotificationWindow();
        try
        {
            // Each group has its own vault; use the one the clicked files are in.
            var settings = UserSettings.Load();
            var vaultFolder = settings.VaultFor(paths[0]) ?? settings.VaultPath;
            if (command == "open" || command == "show")
            {
                if (!string.Equals(vaultFolder, settings.VaultPath, StringComparison.OrdinalIgnoreCase))
                {
                    settings.VaultPath = vaultFolder;
                    settings.Save();
                }
                OpenMainWindow(command == "show" ? paths[0] : null);
                return;
            }

            note.Say("Opening the vault…");
            var vault = await VaultService.OpenAsync(vaultFolder);
            var names = PathList.Format(paths);

            switch (command)
            {
                case "checkout":
                    note.Say($"Checking out {names}…");
                    await FinishAsync(note, await vault.CheckOutAsync(paths, new Progress<string>(s => note.Say(s))), "Checked out");
                    break;

                case "checkin":
                    note.Hide();
                    var exportWarning = await vault.GetExportWarningAsync(paths);
                    if (exportWarning != null &&
                        MessageBox.Show(exportWarning, "Jarvis \u2014 Check in exports?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    {
                        return;
                    }
                    var team = vault.Config.SolidWorksVersion;
                    var dialog = new CheckInWindow("Check In", "Check In", paths.Select(vault.ToRelativePath).ToList(), "", allowKeepCheckedOut: true,
                        footnote: team != null ? $"Close the files in SolidWorks first. Only check in files saved in SolidWorks {team}." : null);
                    if (dialog.ShowDialog() != true) return;
                    note.Say($"Checking in {names}…");
                    await FinishAsync(note, await vault.CheckInAsync(paths, dialog.Comment, dialog.KeepCheckedOut, new Progress<string>(s => note.Say(s))), "Checked in");
                    break;

                case "undo":
                    var answer = MessageBox.Show($"Undo check-out of {names}?\n\nAny changes will be thrown away and replaced with the vault version. " +
                                                 $"A backup copy is kept in {FileUtil.BackupRoot}.",
                        "Jarvis — Undo Check Out", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                    if (answer != MessageBoxResult.OK) return;
                    note.Say($"Undoing check-out of {names}…");
                    await FinishAsync(note, await vault.UndoCheckOutAsync(paths, new Progress<string>(s => note.Say(s))), "Undid check-out of");
                    break;

                case "download":
                    note.Say($"Downloading {names}\u2026");
                    await vault.DownloadAsync(paths, new Progress<string>(s => note.Say(s)));
                    note.Say($"Downloaded {names}.", TimeSpan.FromSeconds(4));
                    await WaitForCloseAsync(note);
                    break;

                case "getlatest":
                    note.Say("Getting latest…");
                    var latest = await vault.GetLatestAsync(new Progress<string>(s => note.Say(s)));
                    note.Say(latest.CheckInsReceived == 0 ? "Already up to date." : $"Got latest: {latest.UpdatedFiles.Count} file(s) updated.", TimeSpan.FromSeconds(4));
                    await WaitForCloseAsync(note);
                    break;
            }
        }
        catch (PdmException ex)
        {
            note.Close();
            new ErrorWindow(ex).ShowDialog();
        }
        catch (Exception ex)
        {
            note.Close();
            new ErrorWindow(new PdmException("Something unexpected went wrong: " + ex.Message, ex.ToString())).ShowDialog();
        }
    }

    private static async Task FinishAsync(NotificationWindow note, OperationResult result, string verb)
    {
        if (result.Failures.Count == 0 && result.Warnings.Count == 0)
        {
            note.Say($"{verb} {PathList.Format(result.Succeeded)}." + (result.Notes.Count > 0 ? "\n" + string.Join("\n", result.Notes) : ""), TimeSpan.FromSeconds(4));
            await WaitForCloseAsync(note);
            return;
        }

        note.Close();
        if (result.Failures.Count == 1 && result.Succeeded.Count == 0 && result.Warnings.Count == 0)
        {
            new ErrorWindow(result.Failures[0].Error).ShowDialog();
            return;
        }
        var lines = new List<string>();
        if (result.Succeeded.Count > 0) lines.Add($"{verb} {PathList.Format(result.Succeeded)}.");
        lines.AddRange(result.Failures.Select(f => "• " + f.Error.Message));
        lines.AddRange(result.Warnings.Select(w => "• " + w));
        lines.AddRange(result.Notes);
        MessageBox.Show(string.Join("\n\n", lines), "Jarvis", MessageBoxButton.OK,
            result.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static Task WaitForCloseAsync(Window window)
    {
        var done = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => done.TrySetResult(true);
        if (!window.IsVisible) done.TrySetResult(true);
        return done.Task;
    }

    /// <summary>Opens the full app, optionally with a file selected.</summary>
    private static void OpenMainWindow(string? select)
    {
        var window = new MainWindow { PendingSelection = select };
        Application.Current.MainWindow = window;
        Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }
}
