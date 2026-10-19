using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CapstonePdm.Core.Config;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Vault;

namespace CapstonePdm.SwAddin;

/// <summary>
/// The "Capstone PDM" task pane: status of the active document plus PDM commands. Watches the
/// active document with a timer (no SolidWorks event wiring, which keeps it version-proof).
/// All vault work goes through the same Core VaultService as the desktop app.
/// </summary>
internal sealed class VaultPane : UserControl
{
    private static readonly Color Green = Color.FromArgb(0x2E, 0x7D, 0x32);
    private static readonly Color Blue = Color.FromArgb(0x15, 0x65, 0xC0);
    private static readonly Color Red = Color.FromArgb(0xC6, 0x28, 0x28);
    private static readonly Color Orange = Color.FromArgb(0xEF, 0x6C, 0x00);
    private static readonly Color Purple = Color.FromArgb(0x6A, 0x1B, 0x9A);
    private static readonly Color Gray = Color.FromArgb(0x75, 0x75, 0x75);
    private static readonly TimeSpan VaultContactInterval = TimeSpan.FromSeconds(60);

    private readonly SolidWorksSession _sw;
    private readonly Timer _timer = new Timer { Interval = 1000 };
    private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checkedOnline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly FlowLayoutPanel _layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
    private readonly Label _banner = new Label { AutoSize = true, Padding = new Padding(6), Visible = false };
    private readonly Label _fileName = new Label { AutoSize = true };
    private readonly Label _state = new Label { AutoSize = true };
    private readonly Label _details = new Label { AutoSize = true, ForeColor = Color.FromArgb(0x44, 0x44, 0x44) };
    private readonly Button _checkOut = NewButton("Check Out");
    private readonly Button _checkIn = NewButton("Check In…");
    private readonly Button _undo = NewButton("Undo Check Out");
    private readonly Button _getLatest = NewButton("Get Latest");
    private readonly Button _refresh = NewButton("Refresh");
    private readonly Button _openApp = NewButton("Open Capstone PDM");
    private readonly Label _status = new Label { AutoSize = true, ForeColor = Gray, Margin = new Padding(3, 10, 3, 3) };

    private VaultService? _vault;
    private string? _vaultProblem;
    private VaultSnapshot? _snapshot;
    private Dictionary<string, VaultFile> _filesByFullPath = new Dictionary<string, VaultFile>(StringComparer.OrdinalIgnoreCase);
    private string? _currentPath;
    private DateTime _lastVaultContact = DateTime.MinValue;
    private bool _busy;

    public VaultPane(SolidWorksSession sw)
    {
        _sw = sw;
        Font = SystemFonts.MessageBoxFont;
        BackColor = SystemColors.Window;

        var title = new Label { Text = "Capstone PDM", AutoSize = true, Font = new Font(Font.FontFamily, 12f, FontStyle.Bold), Margin = new Padding(3, 0, 3, 8) };
        _fileName.Font = new Font(Font, FontStyle.Bold);
        _state.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
        _details.Margin = new Padding(3, 3, 3, 10);

        _layout.Controls.AddRange(new Control[]
        {
            title, _banner, _fileName, _state, _details,
            _checkOut, _checkIn, _undo, Spacer(), _getLatest, _refresh, _openApp, _status,
        });
        Controls.Add(_layout);
        Resize += (_, _) => FitWidths();

        _checkOut.Click += async (_, _) => await CheckOutAsync();
        _checkIn.Click += async (_, _) => await CheckInAsync();
        _undo.Click += async (_, _) => await UndoCheckOutAsync();
        _getLatest.Click += async (_, _) => await GetLatestAsync();
        _refresh.Click += async (_, _) => await RefreshAsync(contactVault: true);
        _openApp.Click += (_, _) => OpenDesktopApp();
        _timer.Tick += async (_, _) => await OnTickAsync();

        UpdateView();
        _timer.Start();
    }

    public void Shutdown()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    // ------------------------------------------------------------ watching the active document

    private async Task OnTickAsync()
    {
        if (_busy) return;

        string? path;
        try
        {
            path = _sw.ActiveDocumentPath;
        }
        catch (Exception)
        {
            return; // SolidWorks is busy (e.g. a modal dialog); try again next tick.
        }

        var documentChanged = !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase);
        if (documentChanged)
        {
            _currentPath = path;
            UpdateView();
            // First time we see a file this session, ask the vault who has it.
            var firstLook = path != null && IsInVault(path) && _checkedOnline.Add(path);
            await RefreshAsync(contactVault: firstLook || VaultContactDue);
        }
        else if (VaultContactDue)
        {
            await RefreshAsync(contactVault: true);
        }
    }

    private bool VaultContactDue => DateTime.Now - _lastVaultContact > VaultContactInterval;

    private async Task RefreshAsync(bool contactVault)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_vault == null) await OpenVaultAsync();
            if (_vault == null) return;

            _snapshot = await _vault.GetSnapshotAsync(contactVault);
            if (contactVault) _lastVaultContact = DateTime.Now;
            _filesByFullPath = _snapshot.Files.ToDictionary(f => _vault.ToFullPath(f.RelativePath), f => f, StringComparer.OrdinalIgnoreCase);
            UpdateView();
            WarnIfCheckedOutByOther();
        }
        catch (PdmException ex)
        {
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            AddinLog.Write("Refresh failed: " + ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task OpenVaultAsync()
    {
        try
        {
            _vault = await VaultService.OpenAsync(UserSettings.Load().VaultPath);
            _vaultProblem = null;
        }
        catch (PdmException ex)
        {
            _vaultProblem = ex.Message;
            _lastVaultContact = DateTime.Now; // don't retry every second
            UpdateView();
        }
    }

    private void WarnIfCheckedOutByOther()
    {
        var file = CurrentFile;
        if (_currentPath == null || file?.State != FileState.CheckedOutByOther || !_warned.Add(_currentPath)) return;

        var since = file.Lock?.LockedAt != null ? $" since {file.Lock.LockedAt.Value.LocalDateTime:ddd MMM d, h:mm tt}" : "";
        MessageBox.Show(_sw.MainWindow,
            $"{file.Name} is checked out by {file.CheckedOutBy}{since}.\n\n" +
            "It's open read-only: you can look at it, but you can't check in changes until they check it in.",
            "Capstone PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ------------------------------------------------------------ view

    private VaultFile? CurrentFile => _currentPath != null && _filesByFullPath.TryGetValue(_currentPath, out var f) ? f : null;

    private VaultFile? FileAt(string fullPath) => _filesByFullPath.TryGetValue(fullPath, out var f) ? f : null;

    private bool IsInVault(string fullPath) =>
        _vault != null && fullPath.StartsWith(_vault.VaultRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void UpdateView()
    {
        SuspendLayout();
        UpdateBanner();

        var file = CurrentFile;
        _fileName.Text = _currentPath != null ? Path.GetFileName(_currentPath) : _sw.ActiveDocument != null ? "Unsaved document" : "No document open";
        _details.Text = "";

        if (_vault == null)
        {
            SetState("Vault not available", Gray);
        }
        else if (_currentPath == null)
        {
            SetState(_sw.ActiveDocument != null ? "Save it inside the vault to add it" : "Open a file from the vault", Gray);
            if (_sw.ActiveDocument != null) _details.Text = $"Vault folder: {_vault.VaultRoot}";
        }
        else if (!IsInVault(_currentPath))
        {
            SetState("Not in the vault", Orange);
            _details.Text = $"Only files inside {_vault.VaultRoot} are managed. Use Save As to put a copy in the vault.";
        }
        else if (file == null)
        {
            SetState(_snapshot == null ? "Checking…" : "Not tracked by the vault", Gray);
        }
        else
        {
            ShowFile(file);
        }

        var state = file?.State;
        var ready = _vault != null && !_busy;
        _checkOut.Enabled = ready && file != null && file.IsLockable && state is FileState.Available or FileState.ModifiedWithoutCheckOut;
        _checkIn.Enabled = ready && state is FileState.CheckedOutByMe or FileState.NotInVault or FileState.Modified;
        _undo.Enabled = ready && state is FileState.CheckedOutByMe or FileState.ModifiedWithoutCheckOut or FileState.Modified;
        _getLatest.Enabled = ready;
        _refresh.Enabled = !_busy;

        FitWidths();
        ResumeLayout();
    }

    private void ShowFile(VaultFile file)
    {
        switch (file.State)
        {
            case FileState.Available:
                SetState(file.IsOutdated ? "Available, newer version in vault" : "Available", file.IsOutdated ? Gray : Green);
                break;
            case FileState.CheckedOutByMe:
                SetState("Checked out to you", Blue);
                break;
            case FileState.CheckedOutByOther:
                SetState("Checked out by " + file.CheckedOutBy, Red);
                break;
            case FileState.ModifiedWithoutCheckOut:
                SetState("Edited WITHOUT check-out!", Orange);
                break;
            case FileState.Modified:
                SetState("Edited (check in to save)", Orange);
                break;
            case FileState.NotInVault:
                SetState("New, not in vault yet", Purple);
                break;
            default:
                SetState(file.State.ToString(), Gray);
                break;
        }

        var lines = new List<string>();
        if (file.Lock?.LockedAt != null) lines.Add($"Checked out {file.Lock.LockedAt.Value.LocalDateTime:ddd MMM d, h:mm tt}");
        if (file.LatestVersion != null)
        {
            lines.Add($"Version {file.Version}, checked in by {file.LatestVersion.Author} on {file.LatestVersion.Date.LocalDateTime:MMM d}");
            if (file.LatestVersion.CommentFirstLine.Length > 0) lines.Add($"“{file.LatestVersion.CommentFirstLine}”");
        }
        if (file.State == FileState.Available && !file.IsOutdated) lines.Add("Check out to edit.");
        if (file.IsOutdated) lines.Add("Click Get Latest to update it.");
        _details.Text = string.Join("\n", lines);
    }

    private void UpdateBanner()
    {
        var lines = new List<string>();
        var color = Color.FromArgb(0xE3, 0xF2, 0xFD);
        if (_vaultProblem != null)
        {
            lines.Add(_vaultProblem);
            color = Color.FromArgb(0xFF, 0xEB, 0xEE);
        }
        else if (_snapshot != null)
        {
            if (_snapshot.IsOffline)
            {
                lines.Add("Offline: showing the last known status.");
                color = Color.FromArgb(0xFF, 0xF3, 0xE0);
            }
            if (_snapshot.PendingCheckIns > 0)
            {
                lines.Add($"{_snapshot.PendingCheckIns} check-in(s) haven't reached the vault yet. Check In again to send them.");
                color = Color.FromArgb(0xFF, 0xF3, 0xE0);
            }
            if (_snapshot.UpdatesAvailable > 0) lines.Add($"The vault has {_snapshot.UpdatesAvailable} new check-in(s). Click Get Latest.");
        }
        _banner.Text = string.Join("\n", lines);
        _banner.BackColor = color;
        _banner.Visible = lines.Count > 0;
    }

    private void SetState(string text, Color color)
    {
        _state.Text = text;
        _state.ForeColor = color;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void FitWidths()
    {
        var width = Math.Max(120, _layout.ClientSize.Width - _layout.Padding.Horizontal - 8);
        foreach (Control c in _layout.Controls)
        {
            if (c is Button || c.Tag as string == "spacer") c.Width = width;
            else if (c is Label label) label.MaximumSize = new Size(width, 0);
        }
    }

    private static Button NewButton(string text) =>
        new Button { Text = text, Height = 30, Margin = new Padding(3, 2, 3, 2), FlatStyle = FlatStyle.System };

    private static Control Spacer() => new Panel { Height = 8, Tag = "spacer" };

    // ------------------------------------------------------------ commands

    private async Task CheckOutAsync()
    {
        var top = _currentPath;
        if (top == null || _vault == null) return;

        var targets = new List<string> { top };
        var references = _sw.GetReferences(top).Where(IsInVault).ToList();
        if (references.Count > 0)
        {
            var items = new List<PickItem> { new PickItem(top, Path.GetFileName(top) + "  (this document)", true) };
            foreach (var path in references.OrderBy(Path.GetFileName))
            {
                var file = FileAt(path);
                if (file == null || !file.IsLockable) continue;
                var locked = file.State == FileState.CheckedOutByOther;
                var label = Path.GetFileName(path) + (file.State switch
                {
                    FileState.CheckedOutByOther => $"  (checked out by {file.CheckedOutBy})",
                    FileState.CheckedOutByMe => "  (already checked out to you)",
                    _ => "",
                });
                items.Add(new PickItem(path, label, false, locked || file.State == FileState.CheckedOutByMe));
            }

            if (items.Count > 1)
            {
                using var picker = new FilePickerForm("Check Out",
                    "Check out this document only, or also the files it references:", items, "Check Out", askComment: false, askKeep: false);
                if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
                targets = picker.SelectedPaths;
            }
        }

        await RunAsync("Checking out…", async progress =>
        {
            // Outdated files must be replaced on disk, which needs SolidWorks to let go of them.
            var outdated = targets.Where(p => FileAt(p)?.IsOutdated == true).ToList();
            var closed = outdated.Count > 0 ? _sw.CloseDocumentsUsing(outdated, CloseMode.RequireSaved) : null;
            OperationResult result;
            try
            {
                result = await _vault.CheckOutAsync(targets, progress);
            }
            finally
            {
                if (closed != null) _sw.Reopen(closed);
            }
            _sw.MakeWritable(result.Succeeded.Select(_vault.ToFullPath));
            return Report(result, "Checked out");
        });
    }

    private async Task CheckInAsync()
    {
        var top = _currentPath;
        if (top == null || _vault == null) return;

        static bool CanCheckIn(VaultFile? f) => f != null && f.State is FileState.CheckedOutByMe or FileState.NotInVault or FileState.Modified;

        var items = new List<PickItem> { new PickItem(top, Path.GetFileName(top) + "  (this document)", true) };
        var newReferences = 0;
        foreach (var path in _sw.GetReferences(top).Where(IsInVault).OrderBy(Path.GetFileName))
        {
            var file = FileAt(path);
            if (!CanCheckIn(file)) continue;
            var isNew = file!.State == FileState.NotInVault;
            if (isNew) newReferences++;
            var label = Path.GetFileName(path) + (isNew ? "  (new)" : file.HasLocalChanges ? "  (edited)" : "  (checked out, unchanged)");
            items.Add(new PickItem(path, label, isNew || file.HasLocalChanges));
        }

        var note = newReferences > 0
            ? $"{newReferences} referenced file(s) are new. Check them in too, or teammates will get missing-reference errors."
            : null;
        using var picker = new FilePickerForm("Check In",
            "SolidWorks saves and closes these files, checks them in, then reopens them.", items, "Check In",
            askComment: true, askKeep: true, note: note);
        if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;

        var targets = picker.SelectedPaths;
        var comment = picker.Comment;
        var keep = picker.KeepCheckedOut;

        await RunAsync("Checking in…", async progress =>
        {
            progress.Report("Saving and closing files…");
            var closed = _sw.CloseDocumentsUsing(targets, CloseMode.Save);
            try
            {
                return Report(await _vault.CheckInAsync(targets, comment, keep, progress), "Checked in");
            }
            finally
            {
                _sw.Reopen(closed);
            }
        });
    }

    private async Task UndoCheckOutAsync()
    {
        var top = _currentPath;
        if (top == null || _vault == null) return;

        var targets = new List<string> { top };
        var mine = _sw.GetReferences(top).Where(IsInVault)
            .Where(p => FileAt(p)?.State is FileState.CheckedOutByMe or FileState.ModifiedWithoutCheckOut)
            .ToList();
        if (mine.Count > 0)
        {
            var items = new List<PickItem> { new PickItem(top, Path.GetFileName(top) + "  (this document)", true) };
            items.AddRange(mine.OrderBy(Path.GetFileName).Select(p => new PickItem(p, Path.GetFileName(p), false)));
            using var picker = new FilePickerForm("Undo Check Out",
                "Your changes to the selected files will be thrown away (a backup copy is kept).", items, "Undo Check Out",
                askComment: false, askKeep: false);
            if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
            targets = picker.SelectedPaths;
        }
        else
        {
            var answer = MessageBox.Show(_sw.MainWindow,
                $"Undo check-out of {Path.GetFileName(top)}?\n\nAny changes will be thrown away and the file will be reloaded from the vault. " +
                $"A backup copy is kept in {FileUtil.BackupRoot}.",
                "Undo Check Out", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
        }

        await RunAsync("Undoing check-out…", async progress =>
        {
            var closed = _sw.CloseDocumentsUsing(targets, CloseMode.DiscardTargets);
            try
            {
                return Report(await _vault.UndoCheckOutAsync(targets, progress), "Undid check-out of");
            }
            finally
            {
                _sw.Reopen(closed);
            }
        });
    }

    private async Task GetLatestAsync()
    {
        if (_vault == null) return;

        await RunAsync("Getting latest…", async progress =>
        {
            GetLatestResult result;
            try
            {
                result = await _vault.GetLatestAsync(progress);
            }
            catch (FileInUseException ex)
            {
                // The usual case inside SolidWorks: the files to update are open. Offer to close and reopen them.
                var paths = ex.Paths.Select(p => Path.IsPathRooted(p) ? p : _vault.ToFullPath(p)).Where(File.Exists).ToList();
                if (paths.Count == 0) throw;
                var answer = MessageBox.Show(_sw.MainWindow,
                    $"{PathList.Format(paths)} {(paths.Count == 1 ? "has" : "have")} a newer version in the vault but {(paths.Count == 1 ? "is" : "are")} open in SolidWorks.\n\n" +
                    "Close them, update them, and reopen them?",
                    "Get Latest", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (answer != DialogResult.OK) return "Get Latest cancelled.";

                var closed = _sw.CloseDocumentsUsing(paths, CloseMode.RequireSaved);
                try
                {
                    result = await _vault.GetLatestAsync(progress);
                }
                finally
                {
                    _sw.Reopen(closed);
                }
            }
            return result.CheckInsReceived == 0 ? "Already up to date." : $"Got latest: {result.UpdatedFiles.Count} file(s) updated.";
        });
    }

    private void OpenDesktopApp()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CapstonePDM", "CapstonePDM.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show(_sw.MainWindow, $"Capstone PDM isn't installed at {exe}. Run Setup.cmd.", "Capstone PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    // ------------------------------------------------------------ plumbing

    private async Task RunAsync(string busyText, Func<IProgress<string>, Task<string>> operation)
    {
        if (_busy || _vault == null) return;
        _busy = true;
        UpdateView();
        SetStatus(busyText);

        var status = "Ready";
        try
        {
            status = await operation(new Progress<string>(SetStatus));
        }
        catch (PdmException ex)
        {
            status = ex.Title;
            MessageBox.Show(_sw.MainWindow, ex.Message, "Capstone PDM — " + ex.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            status = "Error";
            AddinLog.Write("Command failed: " + ex);
            MessageBox.Show(_sw.MainWindow,
                "Something unexpected went wrong: " + ex.Message + $"\n\nDetails were written to {AddinLog.FilePath}.",
                "Capstone PDM", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
        }

        await RefreshAsync(contactVault: true);
        SetStatus(status);
    }

    /// <summary>Shows failures, warnings and notes from a multi-file command; returns the status text.</summary>
    private string Report(OperationResult result, string verb)
    {
        var status = result.Succeeded.Count > 0 ? $"{verb} {PathList.Format(result.Succeeded)}." : "Nothing was done.";

        var sections = new List<string>();
        if (result.Failures.Count > 0) sections.Add(string.Join("\n", result.Failures.Select(f => "• " + f.Error.Message)));
        if (result.Warnings.Count > 0) sections.Add(string.Join("\n", result.Warnings.Select(w => "• " + w)));
        if (result.Notes.Count > 0) sections.Add(string.Join("\n", result.Notes));

        if (sections.Count > 0)
        {
            var header = result.Succeeded.Count > 0 ? status + "\n\n" : "";
            MessageBox.Show(_sw.MainWindow, header + string.Join("\n\n", sections), "Capstone PDM", MessageBoxButtons.OK,
                result.Failures.Count > 0 || result.Warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        return status;
    }
}
