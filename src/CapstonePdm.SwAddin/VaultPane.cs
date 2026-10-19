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
    private static readonly TimeSpan VaultContactInterval = TimeSpan.FromSeconds(60);

    // Badge colors (background, text) per file state.
    private static readonly (Color Back, Color Fore) GreenBadge = (Color.FromArgb(0xE6, 0xF4, 0xEA), Color.FromArgb(0x1B, 0x7F, 0x3B));
    private static readonly (Color Back, Color Fore) BlueBadge = (Color.FromArgb(0xE0, 0xEB, 0xFF), Color.FromArgb(0x1D, 0x4E, 0xD8));
    private static readonly (Color Back, Color Fore) RedBadge = (Color.FromArgb(0xFD, 0xE8, 0xE8), Color.FromArgb(0xB9, 0x1C, 0x1C));
    private static readonly (Color Back, Color Fore) OrangeBadge = (Color.FromArgb(0xFF, 0xF1, 0xE0), Color.FromArgb(0xB4, 0x53, 0x09));
    private static readonly (Color Back, Color Fore) PurpleBadge = (Color.FromArgb(0xF1, 0xE8, 0xFD), Color.FromArgb(0x6D, 0x28, 0xD9));
    private static readonly (Color Back, Color Fore) GrayBadge = (Color.FromArgb(0xEE, 0xF0, 0xF3), Color.FromArgb(0x4B, 0x55, 0x63));
    private static readonly (Color Back, Color Fore) SlateBadge = (Color.FromArgb(0xE2, 0xE8, 0xF0), Color.FromArgb(0x33, 0x41, 0x55));

    private readonly SolidWorksSession _sw;
    private readonly Timer _timer = new Timer { Interval = 1000 };
    private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checkedOnline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly FlowLayoutPanel _layout;
    private readonly PaneHeader _header;
    private readonly NoticeBox _banner;
    private readonly FileCard _card;
    private readonly ModernButton _checkOut;
    private readonly ModernButton _checkIn;
    private readonly ModernButton _undo;
    private readonly ModernButton _freeze;
    private readonly ModernButton _unfreeze;
    private readonly ModernButton _getLatest;
    private readonly ModernButton _refresh;
    private readonly ModernButton _openApp;
    private readonly CaptionLabel _status;

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
        // Sizes are in pixels scaled by hand (WinForms auto-scaling misbehaves inside SolidWorks).
        AutoScaleMode = AutoScaleMode.None;
        using (var g = CreateGraphics()) Theme.Init(g.DpiX);
        BackColor = Theme.Background;
        Font = Theme.Body;

        _layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(Theme.Px(14), Theme.Px(12), Theme.Px(14), Theme.Px(12)),
            BackColor = Theme.Background,
        };
        _header = new PaneHeader(PaneIcon.LoadLogo(Theme.Px(44)), "Capstone PDM");
        _banner = new NoticeBox();
        _card = new FileCard();
        _checkOut = new ModernButton("Check Out", "");
        _checkIn = new ModernButton("Check In", "");
        _undo = new ModernButton("Undo Check Out", "");
        _freeze = new ModernButton("Freeze (admin)", "");
        _unfreeze = new ModernButton("Unfreeze (admin)", "");
        _getLatest = new ModernButton("Get Latest", "");
        _refresh = new ModernButton("Refresh", "");
        _openApp = new ModernButton("Open Capstone PDM", "");
        _status = new CaptionLabel();

        _layout.Controls.AddRange(new Control[]
        {
            _header, _banner, _card,
            _checkIn, _checkOut, _undo, _freeze, _unfreeze,
            new Divider(),
            _getLatest, _refresh, _openApp,
            _status,
        });
        Controls.Add(_layout);
        Resize += (_, _) => FitWidths();

        _checkOut.Click += async (_, _) => await CheckOutAsync();
        _checkIn.Click += async (_, _) => await CheckInAsync();
        _undo.Click += async (_, _) => await UndoCheckOutAsync();
        _freeze.Click += async (_, _) => await FreezeAsync();
        _unfreeze.Click += async (_, _) => await UnfreezeAsync();
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
            // Buttons are enabled only when not busy, so redraw after clearing the flag.
            UpdateView();
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
        if (_currentPath == null || file == null || file.State is not (FileState.CheckedOutByOther or FileState.Frozen) || !_warned.Add(_currentPath)) return;

        string message;
        if (file.State == FileState.Frozen)
        {
            var reason = string.IsNullOrWhiteSpace(file.Freeze?.Reason) ? "" : $": “{file.Freeze!.Reason}”";
            message = $"{file.Name} is frozen by {file.FrozenBy}{reason}.\n\n" +
                      "It's open read-only and can't be changed. Ask an admin if it needs to change.";
        }
        else
        {
            var since = file.Lock?.LockedAt != null ? $" since {file.Lock.LockedAt.Value.LocalDateTime:ddd MMM d, h:mm tt}" : "";
            message = $"{file.Name} is checked out by {file.CheckedOutBy}{since}.\n\n" +
                      "It's open read-only: you can look at it, but you can't check in changes until they check it in.";
        }
        MessageBox.Show(_sw.MainWindow, message, "Capstone PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ------------------------------------------------------------ view

    private VaultFile? CurrentFile => _currentPath != null && _filesByFullPath.TryGetValue(_currentPath, out var f) ? f : null;

    private VaultFile? FileAt(string fullPath) => _filesByFullPath.TryGetValue(fullPath, out var f) ? f : null;

    private bool IsInVault(string fullPath) =>
        _vault != null && fullPath.StartsWith(_vault.VaultRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void UpdateView()
    {
        _layout.SuspendLayout();
        UpdateBanner();
        _header.Subtitle = _vault == null ? "Vault not connected" : $"{_vault.Config.VaultName}  \u2022  {_vault.UserDisplayName}";

        var file = CurrentFile;
        var name = _currentPath != null ? Path.GetFileName(_currentPath) : _sw.ActiveDocument != null ? "Unsaved document" : "No document open";

        if (_vault == null)
        {
            _card.Show(name, "Vault not available", GrayBadge.Back, GrayBadge.Fore, "Click Refresh to try again.");
        }
        else if (_currentPath == null)
        {
            _card.Show(name, _sw.ActiveDocument != null ? "Not saved yet" : "Nothing to show", GrayBadge.Back, GrayBadge.Fore,
                _sw.ActiveDocument != null ? $"Save it inside {_vault.VaultRoot} to add it to the vault." : "Open a file from the vault to see its status.");
        }
        else if (!IsInVault(_currentPath))
        {
            _card.Show(name, "Not in the vault", OrangeBadge.Back, OrangeBadge.Fore,
                $"Only files inside {_vault.VaultRoot} are managed. Use Save As to put a copy in the vault.");
        }
        else if (file == null)
        {
            _card.Show(name, _snapshot == null ? "Checking\u2026" : "Not tracked", GrayBadge.Back, GrayBadge.Fore, "");
        }
        else
        {
            ShowFile(file);
        }

        // Show only the actions that make sense for this file; the main one is highlighted.
        var state = file?.State;
        var ready = _vault != null && !_busy;
        var canCheckIn = state is FileState.CheckedOutByMe or FileState.NotInVault or FileState.Modified;
        var canCheckOut = file != null && file.IsLockable && state is FileState.Available or FileState.ModifiedWithoutCheckOut;
        var canUndo = state is FileState.CheckedOutByMe or FileState.ModifiedWithoutCheckOut or FileState.Modified;

        _checkIn.Visible = canCheckIn;
        _checkOut.Visible = canCheckOut;
        _undo.Visible = canUndo;
        var isAdmin = _vault?.IsAdmin == true;
        _freeze.Visible = isAdmin && file != null && file.IsLockable && !file.HasLocalChanges && state is FileState.Available or FileState.CheckedOutByMe;
        _unfreeze.Visible = isAdmin && state == FileState.Frozen;
        _checkIn.Primary = canCheckIn;
        _checkOut.Primary = canCheckOut && !canCheckIn;
        _checkIn.Enabled = _checkOut.Enabled = _undo.Enabled = _freeze.Enabled = _unfreeze.Enabled = ready;
        _getLatest.Enabled = ready;
        _getLatest.Primary = (_snapshot?.UpdatesAvailable ?? 0) > 0 && !canCheckIn && !canCheckOut;
        _refresh.Enabled = !_busy;

        FitWidths();
        _layout.ResumeLayout();
    }

    private void ShowFile(VaultFile file)
    {
        (string Text, (Color Back, Color Fore) Colors) badge = file.State switch
        {
            FileState.Available when file.IsOutdated => ("Newer version in vault", GrayBadge),
            FileState.Available => ("Available", GreenBadge),
            FileState.CheckedOutByMe => ("Checked out to you", BlueBadge),
            FileState.CheckedOutByOther => ("Checked out by " + file.CheckedOutBy, RedBadge),
            FileState.ModifiedWithoutCheckOut => ("Edited without check-out", OrangeBadge),
            FileState.Modified => ("Edited, not checked in", OrangeBadge),
            FileState.NotInVault => ("New, not in vault yet", PurpleBadge),
            FileState.Frozen => ("Frozen", SlateBadge),
            _ => (file.State.ToString(), GrayBadge),
        };

        var lines = new List<string>();
        if (file.Lock?.LockedAt != null) lines.Add($"Checked out {file.Lock.LockedAt.Value.LocalDateTime:ddd MMM d, h:mm tt}");
        if (file.LatestVersion != null)
        {
            lines.Add($"Version {file.Version}  \u2022  {file.LatestVersion.Author}, {file.LatestVersion.Date.LocalDateTime:MMM d}");
            if (file.LatestVersion.CommentFirstLine.Length > 0) lines.Add($"\u201C{file.LatestVersion.CommentFirstLine}\u201D");
        }
        if (file.State == FileState.Available && !file.IsOutdated) lines.Add("Check out to edit.");
        if (file.State == FileState.ModifiedWithoutCheckOut) lines.Add("Check it out to keep your edits, or Undo to discard them.");
        if (file.IsOutdated) lines.Add("Click Get Latest to update it.");
        if (file.State == FileState.Frozen && file.Freeze != null)
        {
            lines.Clear();
            lines.Add($"Frozen by {file.FrozenBy} on {file.Freeze.At.LocalDateTime:MMM d, yyyy}");
            if (file.Freeze.Reason.Length > 0) lines.Add($"“{file.Freeze.Reason}”");
            lines.Add(_vault?.IsAdmin == true ? "Unfreeze it if it really needs to change." : "It can't be changed. Ask an admin if it needs to.");
            if (file.FreezeLockMissing) lines.Add("Warning: the freeze lock is missing. An admin should freeze it again.");
        }

        _card.Show(file.Name, badge.Text, badge.Colors.Back, badge.Colors.Fore, string.Join("\n", lines));
    }

    private void UpdateBanner()
    {
        var lines = new List<string>();
        var kind = NoticeKind.Info;
        if (_vaultProblem != null)
        {
            lines.Add(_vaultProblem);
            kind = NoticeKind.Error;
        }
        else if (_snapshot != null)
        {
            if (_snapshot.IsOffline)
            {
                lines.Add("Offline: showing the last known status.");
                kind = NoticeKind.Warning;
            }
            if (_snapshot.PendingCheckIns > 0)
            {
                lines.Add($"{_snapshot.PendingCheckIns} check-in(s) haven't reached the vault yet. Check In again to send them.");
                kind = NoticeKind.Warning;
            }
            if (_snapshot.UpdatesAvailable > 0) lines.Add($"The vault has {_snapshot.UpdatesAvailable} new check-in(s). Click Get Latest.");
        }
        _banner.Show(string.Join("\n", lines), kind);
    }

    private void SetStatus(string text) => _status.Text = text;

    private void FitWidths()
    {
        var scrollbar = _layout.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
        var width = Math.Max(Theme.Px(140), _layout.ClientSize.Width - _layout.Padding.Horizontal - scrollbar);
        foreach (Control c in _layout.Controls)
        {
            if (c is IFitWidth fit) fit.FitTo(width);
            else c.Width = width;
        }
    }

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

    private async Task FreezeAsync()
    {
        var top = _currentPath;
        if (top == null || _vault == null) return;

        // Releasing an assembly usually means its parts too: offer them, unticked.
        var items = new List<PickItem> { new PickItem(top, Path.GetFileName(top) + "  (this document)", true) };
        foreach (var path in _sw.GetReferences(top).Where(IsInVault).OrderBy(Path.GetFileName))
        {
            var file = FileAt(path);
            if (file == null || !file.IsLockable) continue;
            var canFreeze = !file.HasLocalChanges && file.State is FileState.Available or FileState.CheckedOutByMe;
            var label = Path.GetFileName(path) + (file.State switch
            {
                FileState.Frozen => "  (already frozen)",
                FileState.CheckedOutByOther => $"  (checked out by {file.CheckedOutBy})",
                _ when file.HasLocalChanges => "  (has changes, check in first)",
                _ => "",
            });
            items.Add(new PickItem(path, label, false, !canFreeze));
        }

        using var picker = new FilePickerForm("Freeze (admin)",
            "Frozen files can't be checked out or changed by anyone until an admin unfreezes them.", items, "Freeze",
            askComment: true, askKeep: false, commentLabel: "Reason (required): why is it frozen?", defaultComment: "Released for production");
        if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
        var targets = picker.SelectedPaths;
        var reason = picker.Comment;

        await RunAsync("Freezing…", async progress =>
        {
            var result = await _vault.FreezeAsync(targets, reason, progress);
            _sw.MakeReadOnly(result.Succeeded.Select(_vault.ToFullPath));
            return Report(result, "Froze");
        });
    }

    private async Task UnfreezeAsync()
    {
        var top = _currentPath;
        if (top == null || _vault == null) return;

        var answer = MessageBox.Show(_sw.MainWindow,
            $"Unfreeze {Path.GetFileName(top)}?\n\nAnyone will be able to check it out and change it again. The team will be told in Discord.",
            "Unfreeze (admin)", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;

        await RunAsync("Unfreezing…", async progress => Report(await _vault.UnfreezeAsync(new[] { top }, progress), "Unfroze"));
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
