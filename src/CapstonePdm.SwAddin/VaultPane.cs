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
    private readonly ModernButton _requestCheckIn;
    private readonly ModernButton _rename;
    private readonly ModernButton _getLatest;
    private readonly ModernButton _refresh;
    private readonly ModernButton _openApp;
    private readonly ModernButton _rebuildIndex;
    private readonly CaptionLabel _status;

    private VaultService? _vault;
    private string? _vaultProblem;
    private VaultSnapshot? _snapshot;
    private Dictionary<string, VaultFile> _filesByFullPath = new Dictionary<string, VaultFile>(StringComparer.OrdinalIgnoreCase);
    private string? _currentPath;
    private DateTime _lastVaultContact = DateTime.MinValue;
    private bool _busy;
    private readonly int _runningYear;
    private readonly Dictionary<string, (DateTime Written, int? Year)> _savedInCache = new Dictionary<string, (DateTime, int?)>(StringComparer.OrdinalIgnoreCase);

    public VaultPane(SolidWorksSession sw)
    {
        _sw = sw;
        try
        {
            _runningYear = sw.RunningYear;
        }
        catch (Exception ex)
        {
            AddinLog.Write("Couldn't read the SolidWorks version: " + ex.Message);
        }
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
        _requestCheckIn = new ModernButton("Request Check-In (admin)", "");
        _rename = new ModernButton("Rename / Move", "");
        _getLatest = new ModernButton("Get Latest", "");
        _refresh = new ModernButton("Refresh", "");
        _openApp = new ModernButton("Open Capstone PDM", "");
        _rebuildIndex = new ModernButton("Rebuild Index", "\uE895");
        _status = new CaptionLabel();

        _layout.Controls.AddRange(new Control[]
        {
            _header, _banner, _card,
            _checkIn, _checkOut, _undo, _rename, _requestCheckIn, _freeze, _unfreeze,
            new Divider(),
            _getLatest, _refresh, _openApp, _rebuildIndex,
            _status,
        });
        Controls.Add(_layout);
        Resize += (_, _) => FitWidths();

        _checkOut.Click += async (_, _) => await CheckOutAsync();
        _checkIn.Click += async (_, _) => await CheckInAsync();
        _undo.Click += async (_, _) => await UndoCheckOutAsync();
        _freeze.Click += async (_, _) => await FreezeAsync();
        _unfreeze.Click += async (_, _) => await UnfreezeAsync();
        _requestCheckIn.Click += async (_, _) => await RequestCheckInAsync();
        _rename.Click += async (_, _) => await RenameAsync();
        _getLatest.Click += async (_, _) => await GetLatestAsync();
        _refresh.Click += async (_, _) => await RefreshAsync(contactVault: true);
        _openApp.Click += (_, _) => OpenDesktopApp();
        _rebuildIndex.Click += async (_, _) => await RebuildIndexAsync();
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
            // Follow a vault switch made in the desktop app's Settings (e.g. capstone vault vs. club vault).
            if (_vault != null && !string.Equals(Path.GetFullPath(UserSettings.Load().VaultPath).TrimEnd('\\'), _vault.VaultRoot, StringComparison.OrdinalIgnoreCase))
            {
                _vault = null;
                _snapshot = null;
                _filesByFullPath = new Dictionary<string, VaultFile>(StringComparer.OrdinalIgnoreCase);
                _checkedOnline.Clear();
                contactVault = true;
            }
            if (_vault == null) await OpenVaultAsync();
            if (_vault == null) return;

            _snapshot = await _vault.GetSnapshotAsync(contactVault);
            if (contactVault) _lastVaultContact = DateTime.Now;
            _filesByFullPath = _snapshot.Files.ToDictionary(f => _vault.ToFullPath(f.RelativePath), f => f, StringComparer.OrdinalIgnoreCase);
            if (_snapshot.CheckInRequestsForMe > 0) await CarryOutCheckInRequestsAsync();
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
        _requestCheckIn.Visible = isAdmin && state == FileState.CheckedOutByOther;
        _rename.Visible = file != null && file.ExistsLocally && IsSolidWorksFile(file.RelativePath) && !file.HasLocalChanges &&
                          state is FileState.Available or FileState.CheckedOutByMe;
        _checkIn.Primary = canCheckIn;
        _checkOut.Primary = canCheckOut && !canCheckIn;
        // Editing in a newer SolidWorks than the team's would save files the others can't open.
        var viewingMilestone = _snapshot?.ViewingMilestone != null;
        var editable = ready && !NewerThanTeam && !viewingMilestone;
        _checkIn.Enabled = _checkOut.Enabled = _freeze.Enabled = _rename.Enabled = editable;
        _undo.Enabled = _unfreeze.Enabled = _requestCheckIn.Enabled = ready && !viewingMilestone;
        _getLatest.Enabled = ready && !viewingMilestone;
        _getLatest.Primary = (_snapshot?.UpdatesAvailable ?? 0) > 0 && !canCheckIn && !canCheckOut;
        _refresh.Enabled = !_busy;
        _rebuildIndex.Enabled = ready;

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
            FileState.Frozen => (file.Revision != null ? $"Frozen \u2022 Rev {file.Revision}" : "Frozen", SlateBadge),
            _ => (file.State.ToString(), GrayBadge),
        };

        var lines = new List<string>();
        if (file.Lock?.LockedAt != null) lines.Add($"Checked out {file.Lock.LockedAt.Value.LocalDateTime:ddd MMM d, h:mm tt}");
        if (file.LatestVersion != null)
        {
            lines.Add($"Version {file.Version}  \u2022  {file.LatestVersion.Author}, {file.LatestVersion.Date.LocalDateTime:MMM d}");
            if (file.LatestVersion.CommentFirstLine.Length > 0) lines.Add($"\u201C{file.LatestVersion.CommentFirstLine}\u201D");
        }
        if (file.Revision != null && file.State != FileState.Frozen) lines.Add($"Last released as Rev {file.Revision} (changes since then aren't released).");
        if (file.State == FileState.Available && !file.IsOutdated) lines.Add("Check out to edit.");
        if (file.State == FileState.ModifiedWithoutCheckOut) lines.Add("Check it out to keep your edits, or Undo to discard them.");
        if (file.IsOutdated) lines.Add("Click Get Latest to update it.");
        if (file.CheckInRequest != null)
        {
            lines.Add(file.State == FileState.CheckedOutByMe
                ? $"{file.CheckInRequestedBy} asked for this file. Your saved version will be checked in automatically."
                : $"Check-in requested by {file.CheckInRequestedBy} on {file.CheckInRequest.RequestedAt.LocalDateTime:MMM d, h:mm tt}");
        }
        if (file.State == FileState.Frozen && file.Freeze != null)
        {
            lines.Clear();
            lines.Add($"Frozen by {file.FrozenBy} on {file.Freeze.At.LocalDateTime:MMM d, yyyy}");
            if (file.Freeze.Reason.Length > 0) lines.Add($"“{file.Freeze.Reason}”");
            lines.Add(_vault?.IsAdmin == true ? "Unfreeze it if it really needs to change." : "It can't be changed. Ask an admin if it needs to.");
            if (file.FreezeLockMissing) lines.Add("Warning: the freeze lock is missing. An admin should freeze it again.");
        }

        var index = file.Index;
        var identity = string.Join(" \u2014 ", new[] { index?.GetProperty(IndexEntry.PartNumberNames), index?.GetProperty(IndexEntry.DescriptionNames) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (identity.Length > 0) lines.Insert(0, identity);
        if (file.UsedIn.Count > 0) lines.Add("Used in: " + PathList.Format(file.UsedIn, 3));
        if (index != null && index.ExternalReferences.Count > 0)
        {
            lines.Add($"\u26A0 References {index.ExternalReferences.Count} file(s) outside the vault: teammates won't have them.");
        }

        if (_vault != null && IsSolidWorksFile(file.RelativePath) && file.ExistsLocally)
        {
            var savedIn = SavedInYear(_vault.ToFullPath(file.RelativePath));
            if (savedIn != null)
            {
                lines.Add(TeamYear != null && savedIn > TeamYear
                    ? $"⚠ Saved in SolidWorks {savedIn}: newer than the team's {TeamYear}, so {TeamYear} users can't open it!"
                    : $"Saved in SolidWorks {savedIn}");
            }
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
        if (_snapshot?.ViewingMilestone != null)
        {
            lines.Insert(0, $"Viewing the milestone \u201C{_snapshot.ViewingMilestone.Name}\u201D: files show how they were then (read-only). " +
                            "Use Return to Latest in Capstone PDM to keep working.");
            if (kind != NoticeKind.Error) kind = NoticeKind.Warning;
        }
        if (NewerThanTeam)
        {
            lines.Insert(0, $"You're using SolidWorks {_runningYear}, but the team works in {TeamYear}. Files saved here can't be opened in {TeamYear}, " +
                            $"so Check Out and Check In are turned off in this version. Open SolidWorks {TeamYear} to edit.");
            if (kind != NoticeKind.Error) kind = NoticeKind.Warning;
        }
        else if (TeamYear != null && _runningYear > 0 && _runningYear < TeamYear)
        {
            lines.Add($"Your SolidWorks ({_runningYear}) is older than the team's ({TeamYear}). Files saved in {TeamYear} won't open here.");
            if (kind == NoticeKind.Info) kind = NoticeKind.Warning;
        }
        _banner.Show(string.Join("\n", lines), kind);
    }

    private int? TeamYear => _vault?.Config.SolidWorksVersion;

    private bool NewerThanTeam => TeamYear != null && _runningYear > TeamYear;

    /// <summary>SolidWorks version that last saved the file, cached until the file changes.</summary>
    private int? SavedInYear(string fullPath)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(fullPath);
        }
        catch (Exception)
        {
            return null;
        }
        if (_savedInCache.TryGetValue(fullPath, out var cached) && cached.Written == written) return cached.Year;
        var year = _sw.SavedInYear(fullPath);
        _savedInCache[fullPath] = (written, year);
        return year;
    }

    private static bool IsSolidWorksFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".sldprt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".sldasm", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".slddrw", StringComparison.OrdinalIgnoreCase);
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

        // Version guard: a file last saved in a newer SolidWorks than the team's can't be opened by the others.
        if (TeamYear != null)
        {
            var tooNew = targets.Where(IsSolidWorksFile).Where(p => SavedInYear(p) > TeamYear).ToList();
            if (tooNew.Count > 0)
            {
                MessageBox.Show(_sw.MainWindow,
                    $"{PathList.Format(tooNew)} {(tooNew.Count == 1 ? "was" : "were")} last saved in a newer SolidWorks than the team's {TeamYear}, " +
                    $"so teammates on {TeamYear} couldn't open {(tooNew.Count == 1 ? "it" : "them")}. Nothing was checked in.\n\n" +
                    $"Fix: Undo Check Out to get the vault version back, then redo the change in SolidWorks {TeamYear}.",
                    "Capstone PDM — Wrong SolidWorks version", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        await RunAsync("Checking in…", async progress =>
        {
            progress.Report("Saving and closing files…");
            // Properties can only be read while documents are open; references and saved-in version come from disk after saving.
            var properties = targets.Where(IsSolidWorksFile).ToDictionary(
                p => p,
                p => { var doc = _sw.FindOpen(p); return doc != null ? _sw.ReadProperties(doc) : FileAt(p)?.Index?.Properties; },
                StringComparer.OrdinalIgnoreCase);
            // 3D previews for the app's viewer, from the open models (unopened ones get theirs from Rebuild Index).
            progress.Report("Saving 3D previews…");
            var previews = new List<string>();
            foreach (var path in targets.Where(SolidWorksSession.CanHavePreview))
            {
                var doc = _sw.FindOpen(path);
                var preview = VaultPaths.PreviewFor(_vault.ToRelativePath(path));
                if (doc != null && _sw.ExportPreview(doc, _vault.ToFullPath(preview))) previews.Add(preview);
            }
            var closed = _sw.CloseDocumentsUsing(targets, CloseMode.Save);
            try
            {
                var index = properties.ToDictionary(p => p.Key, p => (IndexEntry?)_sw.BuildIndexEntry(p.Key, _vault.VaultRoot, p.Value), StringComparer.OrdinalIgnoreCase);
                return Report(await _vault.CheckInAsync(targets, comment, keep, progress, indexUpdates: index, previews: previews), "Checked in");
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
            // Releasing a drawing also saves "<name> Rev X.pdf" next to it, checked in and frozen with the same revision.
            var drawings = targets.Where(p => Path.GetExtension(p).Equals(".slddrw", StringComparison.OrdinalIgnoreCase)).ToList();
            var revisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pdfs = new List<string>();
            var pdfProblems = new List<string>();
            if (drawings.Count > 0)
            {
                var next = await _vault.GetNextRevisionsAsync(drawings);
                foreach (var drawing in drawings)
                {
                    var revision = next[_vault.ToRelativePath(drawing)];
                    var pdf = Path.Combine(Path.GetDirectoryName(drawing)!, $"{Path.GetFileNameWithoutExtension(drawing)} Rev {revision}.pdf");
                    progress.Report($"Saving {Path.GetFileName(pdf)}…");
                    try
                    {
                        _sw.ExportPdf(drawing, pdf);
                        revisions[drawing] = revision;
                        revisions[pdf] = revision;
                        pdfs.Add(pdf);
                    }
                    catch (PdmException ex)
                    {
                        pdfProblems.Add(ex.Message);
                    }
                }
                if (pdfs.Count > 0)
                {
                    progress.Report("Checking in the PDFs…");
                    await _vault.CheckInAsync(pdfs, $"Drawing PDF for release: {reason}", progress: progress);
                }
            }

            var result = await _vault.FreezeAsync(targets.Concat(pdfs).ToList(), reason, progress,
                revisions: revisions.ToDictionary(p => _vault.ToRelativePath(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase));
            result.Warnings.AddRange(pdfProblems.Select(p => p + " The drawing was still frozen, without a PDF."));
            _sw.MakeReadOnly(result.Succeeded.Select(_vault.ToFullPath));
            return Report(result, "Froze");
        });
    }

    /// <summary>
    /// An admin asked this user to check files in. Close any of them that have no unsaved changes (so
    /// Git can read them), check in what's saved on disk, reopen, and tell the user.
    /// </summary>
    private async Task CarryOutCheckInRequestsAsync()
    {
        if (_vault == null || _snapshot == null) return;
        var requested = _snapshot.Files
            .Where(f => f.CheckInRequest != null && f.State == FileState.CheckedOutByMe)
            .Select(f => _vault.ToFullPath(f.RelativePath))
            .ToList();
        if (requested.Count == 0) return;

        SetStatus("Checking in files an admin asked for…");
        var stillOpenWithChanges = requested.Where(p => _sw.FindOpen(p)?.GetSaveFlag() == true).ToList();
        var closed = _sw.CloseIfSaved(requested);
        try
        {
            var result = await _vault.ProcessCheckInRequestsAsync(new Progress<string>(SetStatus));
            if (result.Succeeded.Count > 0 && stillOpenWithChanges.Count > 0)
            {
                result.Warnings.Add($"{PathList.Format(stillOpenWithChanges)} still {(stillOpenWithChanges.Count == 1 ? "has" : "have")} unsaved changes open in SolidWorks " +
                                    "that are NOT in the vault, and you no longer have it checked out. Use Save As to keep a copy outside the vault.");
            }
            if (result.Succeeded.Count > 0 || result.Failures.Count > 0) SetStatus(Report(result, "Checked in (admin request)"));
            _snapshot = await _vault.GetSnapshotAsync(false);
            _filesByFullPath = _snapshot.Files.ToDictionary(f => _vault.ToFullPath(f.RelativePath), f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch (PdmException ex)
        {
            SetStatus("An admin asked you to check in a file, but it couldn't be done yet: " + ex.Message + " It will be tried again in a minute.");
        }
        finally
        {
            _sw.Reopen(closed);
        }
    }

    private async Task RequestCheckInAsync()
    {
        var top = _currentPath;
        var file = CurrentFile;
        if (top == null || _vault == null || file == null) return;

        using var picker = new FilePickerForm("Request Check-In (admin)",
            $"{file.CheckedOutBy}'s PC will check in their last saved version of this file and release it, the next time Capstone PDM or SolidWorks " +
            "is running there. Unsaved changes in their SolidWorks aren't included.",
            new[] { new PickItem(top, Path.GetFileName(top), true, isLocked: false) }, "Request",
            askComment: true, askKeep: false, commentLabel: $"Note to {file.CheckedOutBy} (required): why do you need it?",
            defaultComment: "Needed by another teammate");
        if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
        var note = picker.Comment;

        await RunAsync("Requesting check-in…", async progress =>
            Report(await _vault.RequestCheckInAsync(top, note, progress), "Requested check-in of"));
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

    /// <summary>
    /// Renames/moves the active file and repoints every assembly, drawing and derived part that uses
    /// it, then checks the renamed file and all of those in together.
    /// </summary>
    private async Task RenameAsync()
    {
        var oldPath = _currentPath;
        var file = CurrentFile;
        if (oldPath == null || _vault == null || _snapshot == null || file == null) return;

        using var form = new RenameForm(oldPath, _vault.VaultRoot);
        if (form.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
        var newPath = form.NewPath;

        // Find everything that references this file. Read from the files themselves (not the index) so nothing is missed.
        SetStatus("Finding files that use it…");
        var candidates = _snapshot.Files
            .Where(f => f.ExistsLocally && IsSolidWorksFile(f.RelativePath))
            .Select(f => _vault.ToFullPath(f.RelativePath))
            .Where(p => !string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var parents = new List<string>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (i % 10 == 0)
            {
                SetStatus($"Finding files that use it ({i + 1} of {candidates.Count})…");
                await Task.Delay(1);
            }
            if (_sw.GetReferences(candidates[i], allLevels: false, includeParts: true).Any(r => string.Equals(r, oldPath, StringComparison.OrdinalIgnoreCase)))
            {
                parents.Add(candidates[i]);
            }
        }
        SetStatus("");

        var blocked = parents.Select(p => (Path: p, File: FileAt(p)))
            .Where(x => x.File != null && x.File.State is FileState.CheckedOutByOther or FileState.Frozen or FileState.ModifiedWithoutCheckOut)
            .ToList();
        if (blocked.Count > 0)
        {
            MessageBox.Show(_sw.MainWindow,
                "These files use it but can't be updated right now:\n\n" +
                string.Join("\n", blocked.Select(b => $"• {Path.GetFileName(b.Path)}: " + (b.File!.State switch
                {
                    FileState.CheckedOutByOther => $"checked out by {b.File.CheckedOutBy}",
                    FileState.Frozen => "frozen (an admin must unfreeze it)",
                    _ => "edited without check-out",
                }))) +
                "\n\nNothing was renamed.",
                "Rename / Move", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var oldRel = _vault.ToRelativePath(oldPath);
        var newRel = _vault.ToRelativePath(newPath);
        var confirm = $"Rename {oldRel}\nto {newRel}?";
        confirm += parents.Count > 0
            ? $"\n\nThese {parents.Count} file(s) use it and will be updated and checked in with it:\n" + string.Join("\n", parents.Select(p => "• " + _vault.ToRelativePath(p)))
            : "\n\nNo other vault files use it.";
        if (MessageBox.Show(_sw.MainWindow, confirm, "Rename / Move", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        await RunAsync("Renaming…", async progress =>
        {
            // 1. Check out the file and every tracked parent (new, never-checked-in parents don't need it).
            var toCheckOut = new[] { oldPath }.Concat(parents)
                .Where(p => FileAt(p)?.State is FileState.Available)
                .ToList();
            if (toCheckOut.Count > 0)
            {
                var checkOut = await _vault.CheckOutAsync(toCheckOut, progress);
                if (checkOut.Failures.Count > 0) throw checkOut.Failures[0].Error;
            }

            // 2. Close everything involved so SolidWorks lets go of the files.
            progress.Report("Closing files…");
            var involved = new[] { oldPath }.Concat(parents).ToList();
            var closed = _sw.CloseDocumentsUsing(involved, CloseMode.Save);
            var replaced = new List<string>();
            var moved = false;
            try
            {
                // 3. Move the file, then repoint each parent at the new location.
                progress.Report("Updating references…");
                Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                File.Move(oldPath, newPath);
                moved = true;
                foreach (var parent in parents)
                {
                    if (!_sw.ReplaceReference(parent, oldPath, newPath))
                    {
                        throw new PdmException($"SolidWorks couldn't update the reference in {Path.GetFileName(parent)}.");
                    }
                    replaced.Add(parent);
                }
            }
            catch
            {
                // Put everything back the way it was.
                foreach (var parent in replaced) _sw.ReplaceReference(parent, newPath, oldPath);
                if (moved && File.Exists(newPath) && !File.Exists(oldPath)) File.Move(newPath, oldPath);
                _sw.Reopen(closed);
                throw;
            }

            // 4. Check in the rename (old path removed, new path added) with every updated parent, plus their index entries.
            var index = new Dictionary<string, IndexEntry?>(StringComparer.OrdinalIgnoreCase)
            {
                [oldPath] = null,
                [newPath] = _sw.BuildIndexEntry(newPath, _vault.VaultRoot, file.Index?.Properties),
            };
            foreach (var parent in parents) index[parent] = _sw.BuildIndexEntry(parent, _vault.VaultRoot, FileAt(parent)?.Index?.Properties);

            // The 3D preview moves with the file.
            var oldPreview = VaultPaths.PreviewFor(oldRel);
            var newPreview = VaultPaths.PreviewFor(newRel);
            var previews = new List<string>();
            if (File.Exists(_vault.ToFullPath(oldPreview)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_vault.ToFullPath(newPreview))!);
                File.Move(_vault.ToFullPath(oldPreview), _vault.ToFullPath(newPreview));
                previews.Add(oldPreview);
                previews.Add(newPreview);
            }

            var reopen = new ClosedDocuments(string.Equals(closed.ActivePath, oldPath, StringComparison.OrdinalIgnoreCase) ? newPath : closed.ActivePath);
            reopen.Paths.AddRange(closed.Paths.Select(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase) ? newPath : p));
            try
            {
                var result = await _vault.CheckInAsync(new[] { oldPath, newPath }.Concat(parents).ToList(),
                    $"Renamed {oldRel} to {newRel}", progress: progress, indexUpdates: index, previews: previews);
                return Report(result, "Renamed");
            }
            catch (PdmException ex)
            {
                throw new PdmException(ex.Message + "\n\nThe rename is done on this PC but isn't in the vault yet. Select the renamed file and the files " +
                                       "that use it in Capstone PDM and click Check In to finish.", ex.Detail, ex);
            }
            finally
            {
                _sw.Reopen(reopen);
            }
        });
    }

    /// <summary>Reads references, properties and saved-in version of every SolidWorks file in the vault into the index.</summary>
    private async Task RebuildIndexAsync()
    {
        if (_vault == null || _snapshot == null) return;
        var files = _snapshot.Files.Where(f => f.ExistsLocally && IsSolidWorksFile(f.RelativePath)).ToList();
        if (files.Count == 0)
        {
            SetStatus("No SolidWorks files in the vault yet.");
            return;
        }

        var answer = MessageBox.Show(_sw.MainWindow,
            $"Rebuild the vault index for {files.Count} SolidWorks file(s)?\n\n" +
            "Each file is opened in the background (read-only) to read its properties and references, so this can take a few minutes. " +
            "The results show up for everyone: part numbers, descriptions, materials, where-used and saved-in versions.",
            "Rebuild Index", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;

        await RunAsync("Indexing\u2026", async progress =>
        {
            var entries = new Dictionary<string, IndexEntry?>(StringComparer.OrdinalIgnoreCase);
            var previews = new List<string>();
            for (var i = 0; i < files.Count; i++)
            {
                progress.Report($"Indexing {i + 1} of {files.Count}: {files[i].Name}");
                await Task.Delay(1); // let the pane repaint between files
                var full = _vault.ToFullPath(files[i].RelativePath);
                var preview = SolidWorksSession.CanHavePreview(full) ? VaultPaths.PreviewFor(files[i].RelativePath) : null;
                entries[files[i].RelativePath] = _sw.IndexFile(full, _vault.VaultRoot, preview == null ? null : _vault.ToFullPath(preview), out var previewSaved);
                if (previewSaved) previews.Add(preview!);
            }
            await _vault.UpdateIndexAsync(entries, progress, previews: previews);
            return $"Indexed {entries.Count} file(s) and saved {previews.Count} 3D preview(s).";
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
