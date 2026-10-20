using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using SynchronizationContext = System.Threading.SynchronizationContext;
using System.Threading.Tasks;
using System.Windows.Forms;
using Jarvis.Core.Config;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;

namespace Jarvis.SwAddin;

/// <summary>
/// The "Jarvis" task pane: status of the active document, the tree of files it uses, and PDM
/// commands. Watches the active document with a timer (no SolidWorks event wiring, which keeps it
/// version-proof): switching, opening, closing or saving a document rebuilds the tree.
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
    private static readonly (Color Back, Color Fore) TealBadge = (Color.FromArgb(0xCC, 0xFB, 0xF1), Color.FromArgb(0x0F, 0x76, 0x6E));

    private readonly SolidWorksSession _sw;
    private readonly Timer _timer = new Timer { Interval = 1000 };
    private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checkedOnline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly FlowLayoutPanel _layout;
    private readonly PaneHeader _header;
    private readonly NoticeBox _banner;
    private readonly FileCard _card;
    private readonly CaptionLabel _treeLabel;
    private readonly FileTreeView _tree;
    private readonly AssemblySaveGuard _saveGuard;
    /// <summary>Active document and when it was last saved, as of the last tree rebuild.</summary>
    private string? _treeStamp;
    private readonly ModernButton _checkOut;
    private readonly ModernButton _checkIn;
    private readonly ModernButton _undo;
    private readonly ModernButton _freeze;
    private readonly ModernButton _unfreeze;
    private readonly ModernButton _requestCheckIn;
    private readonly ModernButton _submitReview;
    private readonly ModernButton _exportBom;
    private readonly ModernButton _withdrawReview;
    private readonly ModernButton _approveReview;
    private readonly ModernButton _rejectReview;
    private readonly ModernButton _rename;
    private readonly ModernButton _downloadRefs;
    private readonly ModernButton _watch;
    private List<string> _missingRefs = new List<string>();
    private readonly ModernButton _getLatest;
    private readonly ModernButton _refresh;
    private readonly ModernButton _openApp;
    private readonly ModernButton _rebuildIndex;
    private readonly CaptionLabel _status;
    private readonly ActivityBar _activity;

    private VaultService? _vault;
    /// <summary>Vaults opened this session, by folder, so switching between groups' files is quick.</summary>
    private readonly Dictionary<string, VaultService> _openedVaults = new Dictionary<string, VaultService>(StringComparer.OrdinalIgnoreCase);
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
        _header = new PaneHeader(PaneIcon.LoadLogo(Theme.Px(44)), "Jarvis");
        _banner = new NoticeBox();
        _card = new FileCard();
        _treeLabel = new CaptionLabel("Files in use");
        _tree = new FileTreeView
        {
            StatusOf = node => node.RelativePath != null && _vault != null ? FileAt(_vault.ToFullPath(node.RelativePath)) : null,
        };
        _tree.TickedChanged += (_, _) => UpdateView();
        _tree.ExtraMenu = VersionMenu;
        _saveGuard = new AssemblySaveGuard(sw);
        _saveGuard.Saved += message => { SetStatus(message); _treeStamp = null; };
        _checkOut = new ModernButton("Check Out", "");
        _checkIn = new ModernButton("Check In", "");
        _undo = new ModernButton("Undo Check Out", "");
        _freeze = new ModernButton("Freeze (admin)", "");
        _unfreeze = new ModernButton("Unfreeze (admin)", "");
        _requestCheckIn = new ModernButton("Request Check-In (admin)", "");
        _rename = new ModernButton("Rename / Move", "");
        _downloadRefs = new ModernButton("Download missing parts", "\uE896");
        _submitReview = new ModernButton("Submit for review", "\uE8FB");
        _withdrawReview = new ModernButton("Withdraw from review", "");
        _approveReview = new ModernButton("Approve & release (admin)", "");
        _rejectReview = new ModernButton("Reject (admin)", "");
        _watch = new ModernButton("Notify me when it's free", "\uEA8F");
        _getLatest = new ModernButton("Get Latest", "");
        _refresh = new ModernButton("Refresh", "");
        _openApp = new ModernButton("Open Jarvis", "");
        _rebuildIndex = new ModernButton("Rebuild Index", "\uE895");
        _exportBom = new ModernButton("Export BOM (Excel)", "\uE9F9");
        _status = new CaptionLabel();
        _activity = new ActivityBar();

        _layout.Controls.AddRange(new Control[]
        {
            _header, _activity, _status, _banner, _card, _treeLabel, _tree,
            _downloadRefs, _checkIn, _checkOut, _undo, _watch, _approveReview, _rejectReview, _withdrawReview, _submitReview, _rename, _requestCheckIn, _freeze, _unfreeze,
            new Divider(),
            _getLatest, _refresh, _exportBom, _openApp, _rebuildIndex,
        });
        Controls.Add(_layout);
        Resize += (_, _) => FitWidths();

        _checkOut.Click += async (_, _) => { EnsureUiContext(); await CheckOutAsync(); };
        _checkIn.Click += async (_, _) => { EnsureUiContext(); await CheckInAsync(); };
        _undo.Click += async (_, _) => { EnsureUiContext(); await UndoCheckOutAsync(); };
        _freeze.Click += async (_, _) => { EnsureUiContext(); await FreezeAsync(); };
        _unfreeze.Click += async (_, _) => { EnsureUiContext(); await UnfreezeAsync(); };
        _requestCheckIn.Click += async (_, _) => { EnsureUiContext(); await RequestCheckInAsync(); };
        _rename.Click += async (_, _) => { EnsureUiContext(); await RenameAsync(); };
        _submitReview.Click += async (_, _) => { EnsureUiContext(); await SubmitReviewAsync(); };
        _withdrawReview.Click += async (_, _) => { EnsureUiContext(); await WithdrawReviewAsync(); };
        _approveReview.Click += async (_, _) => { EnsureUiContext(); await ApproveReviewAsync(); };
        _rejectReview.Click += async (_, _) => { EnsureUiContext(); await RejectReviewAsync(); };
        _downloadRefs.Click += async (_, _) => { EnsureUiContext(); await DownloadMissingPartsAsync(); };
        _watch.Click += (_, _) => ToggleWatch();
        _getLatest.Click += async (_, _) => { EnsureUiContext(); await GetLatestAsync(); };
        _refresh.Click += async (_, _) => { EnsureUiContext(); await RefreshAsync(contactVault: true); };
        _openApp.Click += (_, _) => OpenDesktopApp();
        _rebuildIndex.Click += async (_, _) => { EnsureUiContext(); await RebuildIndexAsync(); };
        _exportBom.Click += (_, _) => ExportBom();
        _timer.Tick += async (_, _) => await OnTickAsync();

        UpdateView();
        _timer.Start();
    }

    /// <summary>Right-clicks on the pane itself (between controls) shouldn't open SolidWorks' toolbar menu either.</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x007B) return; // WM_CONTEXTMENU
        base.WndProc(ref m);
    }

    public void Shutdown()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    // ------------------------------------------------------------ watching the active document

    /// <summary>
    /// SolidWorks sometimes calls add-in code without WinForms' synchronization context, and then code
    /// after an await resumes on a background thread and can't touch the pane. Put it back at every
    /// entry point (timer, buttons, menus) so awaits return to the UI thread.
    /// </summary>
    private void EnsureUiContext()
    {
        if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
    }

    /// <summary>Runs on the UI thread: directly if already there, else posted to it.</summary>
    private bool OffUiThread(Action action)
    {
        if (!InvokeRequired) return false;
        if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
        return true;
    }

    private async Task OnTickAsync()
    {
        EnsureUiContext();
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
        if (documentChanged) _currentPath = path;
        RebuildTreeIfChanged();
        if (documentChanged)
        {
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

    /// <summary>
    /// Re-reads the active document's components when it changes: switching, opening or closing a
    /// document, components finishing loading or being added/removed, or saving. Status-only changes
    /// just repaint.
    /// </summary>
    private void RebuildTreeIfChanged()
    {
        if (OffUiThread(RebuildTreeIfChanged)) return;
        var path = _currentPath;
        var doc = path != null ? _sw.FindOpen(path) : null;
        string? stamp = null;
        if (path != null && _vault != null)
        {
            long written = 0;
            try
            {
                written = File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception)
            {
                // Can't read the date; the path and component count still tell us enough.
            }
            stamp = $"{path}|{written}|{(doc != null ? SolidWorksSession.ComponentCount(doc) : -1)}";
        }
        if (stamp == _treeStamp) return;
        _treeStamp = stamp;

        FileTreeNode? root = null;
        if (doc != null && _vault != null)
        {
            if (SolidWorksSession.ComponentCount(doc) > 0) _saveGuard.Watch(doc);
            try
            {
                root = FileTree.Build(path!, _sw.GetFilesInUse(doc), _vault.VaultRoot);
            }
            catch (Exception ex)
            {
                AddinLog.Write("Couldn't read the files in use: " + ex);
            }
        }
        _tree.Show(root);
        var count = root?.DistinctFiles().Count() ?? 0;
        var outside = root?.OutsideVault().Select(n => n.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        _treeLabel.Text = root == null ? "" : outside > 0
            ? $"Files in use: {count}  \u2022  \u26A0 {outside} outside the vault"
            : $"Files in use: {count}";
        _tree.Visible = _treeLabel.Visible = root != null;
        FitWidths();
    }

    private bool VaultContactDue => DateTime.Now - _lastVaultContact > VaultContactInterval;

    private async Task RefreshAsync(bool contactVault)
    {
        EnsureUiContext();
        if (_busy) return;
        _busy = true;
        var showActivity = contactVault || _vault == null;
        if (showActivity) StartActivity(_vault == null ? "Opening the vault\u2026" : "Checking the vault\u2026");
        try
        {
            // Work in the vault that holds the open file (each group has its own vault folder). A file
            // outside every vault keeps the current one; with no vault yet, use the app's current vault.
            var settings = UserSettings.Load();
            var wanted = settings.VaultFor(_currentPath) ?? _vault?.VaultRoot ?? settings.VaultPath;
            if (_vault != null && !string.Equals(wanted, _vault.VaultRoot, StringComparison.OrdinalIgnoreCase))
            {
                _vault = null;
                _snapshot = null;
                _filesByFullPath = new Dictionary<string, VaultFile>(StringComparer.OrdinalIgnoreCase);
                _treeStamp = null;
                contactVault = true;
            }
            if (_vault == null) await OpenVaultAsync(wanted);
            if (_vault == null) return;

            _snapshot = await _vault.GetSnapshotAsync(contactVault);
            if (contactVault) _lastVaultContact = DateTime.Now;
            _filesByFullPath = _snapshot.Files.ToDictionary(f => _vault.ToFullPath(f.RelativePath), f => f, StringComparer.OrdinalIgnoreCase);
            if (_snapshot.CheckInRequestsForMe > 0) await CarryOutCheckInRequestsAsync();
            if (contactVault)
            {
                var nowFree = WatchList.TakeNowFree(_vault.VaultRoot, _snapshot);
                if (nowFree.Count > 0)
                {
                    MessageBox.Show(_sw.MainWindow, $"{PathList.Format(nowFree.Select(f => f.RelativePath))} {(nowFree.Count == 1 ? "is" : "are")} free now. You can check {(nowFree.Count == 1 ? "it" : "them")} out.",
                        "Jarvis", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            _missingRefs = _snapshot.DownloadFolders != null && _currentPath != null && SolidWorksSession.HasReferences(_currentPath)
                ? _sw.GetReferences(_currentPath).Where(IsInVault).Where(r => FileAt(r)?.NotDownloaded == true).ToList()
                : new List<string>();
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
            if (showActivity) StopActivity();
            // The tree depends on which vault is open (paths outside it are flagged), so re-check it.
            RebuildTreeIfChanged();
            // Buttons are enabled only when not busy, so redraw after clearing the flag.
            UpdateView();
        }
    }

    private async Task OpenVaultAsync(string folder)
    {
        try
        {
            if (!_openedVaults.TryGetValue(folder, out var vault))
            {
                vault = await VaultService.OpenAsync(folder);
                _openedVaults[folder] = vault;
            }
            _vault = vault;
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
        MessageBox.Show(_sw.MainWindow, message, "Jarvis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ------------------------------------------------------------ view

    private VaultFile? CurrentFile => _currentPath != null && _filesByFullPath.TryGetValue(_currentPath, out var f) ? f : null;

    private VaultFile? FileAt(string fullPath) => _filesByFullPath.TryGetValue(fullPath, out var f) ? f : null;

    private bool IsInVault(string fullPath) =>
        _vault != null && fullPath.StartsWith(_vault.VaultRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void UpdateView()
    {
        if (OffUiThread(UpdateView)) return;
        _layout.SuspendLayout();
        try
        {
            UpdateViewCore();
        }
        catch (Exception ex)
        {
            AddinLog.Write("Updating the pane failed: " + ex);
        }
        finally
        {
            // Always resume, or the pane stays grey.
            _layout.ResumeLayout();
        }
    }

    private void UpdateViewCore()
    {
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
        // Check Out / Check In / Undo cover every file in the tree, not just the active document.
        var treeFiles = TreeFiles();
        var checkOutCount = treeFiles.Count(CanCheckOutFile);
        var checkInCount = treeFiles.Count(CanCheckInFile);
        var undoCount = treeFiles.Count(CanUndoFile);
        var canCheckIn = checkInCount > 0;
        var canCheckOut = checkOutCount > 0;
        var canUndo = undoCount > 0;
        _checkOut.Text = ActionLabel("Check Out", Ticked(CanCheckOutFile).Count, checkOutCount, "available");
        _checkIn.Text = ActionLabel("Check In", Ticked(CanCheckInFile).Count, checkInCount, "to check in");
        _undo.Text = ActionLabel("Undo Check Out", Ticked(CanUndoFile).Count, undoCount, "checked out");

        _checkIn.Visible = canCheckIn;
        _checkOut.Visible = canCheckOut;
        _undo.Visible = canUndo;
        var isAdmin = _vault?.IsAdmin == true;
        _freeze.Visible = isAdmin && file != null && file.IsLockable && !file.HasLocalChanges && state is FileState.Available or FileState.CheckedOutByMe;
        _unfreeze.Visible = isAdmin && state == FileState.Frozen;
        _requestCheckIn.Visible = isAdmin && state == FileState.CheckedOutByOther;
        _submitReview.Visible = file != null && file.IsLockable && !file.HasLocalChanges && state is FileState.Available or FileState.CheckedOutByMe;
        var mySubmission = string.Equals(file?.Review?.SubmittedBy, _vault?.GitHubLogin, StringComparison.OrdinalIgnoreCase);
        _withdrawReview.Visible = state == FileState.InReview && (mySubmission || isAdmin);
        _approveReview.Visible = _rejectReview.Visible = isAdmin && state == FileState.InReview;
        _approveReview.Primary = _approveReview.Visible;
        _downloadRefs.Visible = _missingRefs.Count > 0;
        _watch.Visible = state == FileState.CheckedOutByOther;
        if (_watch.Visible && _vault != null && file != null)
        {
            _watch.Text = WatchList.IsWatching(_vault.VaultRoot, file.RelativePath) ? "Stop notifying me" : "Notify me when it's free";
        }
        _downloadRefs.Enabled = ready && _snapshot?.ViewingMilestone == null;
        _rename.Visible = file != null && file.ExistsLocally && IsSolidWorksFile(file.RelativePath) && !file.HasLocalChanges &&
                          state is FileState.Available or FileState.CheckedOutByMe;
        _checkIn.Primary = canCheckIn;
        _checkOut.Primary = canCheckOut && !canCheckIn;
        // Editing in a newer SolidWorks than the team's would save files the others can't open.
        var viewingMilestone = _snapshot?.ViewingMilestone != null;
        var editable = ready && !NewerThanTeam && !viewingMilestone;
        _checkIn.Enabled = _checkOut.Enabled = _freeze.Enabled = _rename.Enabled = editable;
        _undo.Enabled = _unfreeze.Enabled = _requestCheckIn.Enabled = ready && !viewingMilestone;
        _submitReview.Enabled = _withdrawReview.Enabled = _approveReview.Enabled = _rejectReview.Enabled = ready && !viewingMilestone;
        _getLatest.Enabled = ready && !viewingMilestone;
        _getLatest.Primary = (_snapshot?.UpdatesAvailable ?? 0) > 0 && !canCheckIn && !canCheckOut;
        _refresh.Enabled = !_busy;
        _rebuildIndex.Enabled = ready;
        _exportBom.Visible = _currentPath != null && Path.GetExtension(_currentPath).Equals(".sldasm", StringComparison.OrdinalIgnoreCase);
        _exportBom.Enabled = !_busy;

        if (_tree.Visible) _tree.RefreshStatus();
        FitWidths();
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
            FileState.InReview => ("In review", TealBadge),
            _ => (file.State.ToString(), GrayBadge),
        };

        var lines = new List<string>();
        if (file.Lock?.LockedAt != null) lines.Add($"Checked out {file.Lock.LockedAt.Value.LocalDateTime:ddd MMM d, h:mm tt}");
        if (file.LatestVersion != null)
        {
            lines.Add($"Version {file.VersionText}  \u2022  {file.LatestVersion.Author}, {file.LatestVersion.Date.LocalDateTime:MMM d}");
            if (file.LatestVersion.CommentFirstLine.Length > 0) lines.Add($"\u201C{file.LatestVersion.CommentFirstLine}\u201D");
        }
        if (file.Revision != null && file.State != FileState.Frozen) lines.Add($"Last released as Rev {file.Revision} (changes since then aren't released).");
        if (file.PinnedVersion != null)
        {
            lines.Add($"This PC uses version {file.PinnedVersion.Version} (the vault has {file.VaultVersion}). Right-click it under Files in use to return to the latest.");
        }
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

        if (file.State == FileState.InReview && file.Review != null)
        {
            lines.Clear();
            lines.Add($"Submitted for review by {file.ReviewSubmittedBy} on {file.Review.SubmittedAt.LocalDateTime:MMM d, h:mm tt}");
            if (file.Review.Note.Length > 0) lines.Add($"\u201C{file.Review.Note}\u201D");
            lines.Add(_vault?.IsAdmin == true
                ? "Approve to release it (freeze with the next revision), or reject it with a reason."
                : "Nobody can change it until an admin approves or rejects it.");
        }

        var index = file.Index;
        var identity = string.Join(" \u2014 ", new[] { index?.GetProperty(IndexEntry.PartNumberNames), index?.GetProperty(IndexEntry.DescriptionNames) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (identity.Length > 0) lines.Insert(0, identity);
        if (file.UsedIn.Count > 0) lines.Add("Used in: " + PathList.Format(file.UsedIn, 3));
        if (file.ChangedSinceDrawing.Count > 0) lines.Add($"\u26A0 May be out of date: {PathList.Format(file.ChangedSinceDrawing)} changed after this drawing was last checked in.");
        if (file.StaleDrawings.Count > 0) lines.Add($"\u26A0 {PathList.Format(file.StaleDrawings)} not updated since this changed.");
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
                            "Use Return to Latest in Jarvis to keep working.");
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

    /// <summary>Shows the activity bar; the status line hides meanwhile so the text isn't shown twice.</summary>
    private void StartActivity(string text)
    {
        _activity.Start(text);
        _status.Visible = false;
    }

    private void StopActivity()
    {
        _activity.Stop();
        _status.Visible = true;
    }

    private void SetStatus(string text)
    {
        if (OffUiThread(() => SetStatus(text))) return;
        _status.Text = text;
        _activity.SetText(text);
    }

    /// <summary>
    /// Right-click items for a tree row: put this instance (or the ticked instances of the same part) on
    /// another version, live in the open assembly. Other instances, other assemblies and the part file
    /// itself are untouched.
    /// </summary>
    private IEnumerable<ToolStripItem> VersionMenu(FileTreeNode node)
    {
        if (_vault == null || _busy || _snapshot?.ViewingMilestone != null || node.IsOutsideVault) yield break;
        var file = FileAt(node.FullPath);
        if (file?.PinnedVersion != null)
        {
            // Left over from the earlier whole-file switch.
            yield return new ToolStripMenuItem($"Return the file to the latest version (this PC uses {file.PinnedVersion.Version})", null,
                async (_, _) => await ReturnToLatestAsync(node.FullPath));
        }
        if (node.InstanceName.Length == 0) yield break; // only component instances in an assembly

        var original = VersionCopies.OriginalOf(node.FullPath) ?? node.FullPath;
        var sameParts = _tree.TickedInstances
            .Where(n => n.InstanceName.Length > 0 && string.Equals(VersionCopies.OriginalOf(n.FullPath) ?? n.FullPath, original, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var targets = sameParts.Any(n => n.InstanceName == node.InstanceName) ? sameParts : new List<FileTreeNode> { node };
        var which = targets.Count == 1 ? targets[0].DisplayName : $"the {targets.Count} ticked instances";

        yield return new ToolStripMenuItem($"Use another version for {which}\u2026", null, async (_, _) => await UseVersionForInstancesAsync(targets, original, which));
        var onCopies = targets.Where(n => VersionCopies.IsVersionCopy(n.FullPath)).ToList();
        if (onCopies.Count > 0)
        {
            var whichCopies = onCopies.Count == 1 ? onCopies[0].DisplayName : $"{onCopies.Count} instances";
            yield return new ToolStripMenuItem($"Return {whichCopies} to the latest version", null, async (_, _) => await ReplaceInstancesAsync(onCopies, original, null, whichCopies));
        }
    }

    private async Task UseVersionForInstancesAsync(List<FileTreeNode> targets, string original, string which)
    {
        EnsureUiContext();
        if (_vault == null) return;
        var file = FileAt(original);
        IReadOnlyList<FileVersion> history;
        try
        {
            history = await _vault.GetHistoryAsync(original);
        }
        catch (PdmException ex)
        {
            SetStatus(ex.Message);
            return;
        }
        if (history.Count < 2)
        {
            SetStatus($"{Path.GetFileName(original)} has only one version so far.");
            return;
        }
        var current = VersionCopies.VersionOf(targets[0].FullPath) ?? file?.LatestLocalVersion ?? 0;
        using var picker = new VersionPickerForm(Path.GetFileName(original), history, current, file?.VaultVersion ?? history.Max(v => v.Version),
            $"Only {which} change, right now, in this assembly. Other instances, other assemblies and the part itself stay as they are. " +
            "If the assembly is checked out to you, save and check it in to keep it that way for everyone.");
        if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK || picker.SelectedVersion == null) return;
        await ReplaceInstancesAsync(targets, original, picker.SelectedVersion, which);
    }

    /// <summary>Points instances at a version copy (or back at the original file when <paramref name="version"/> is null or the latest).</summary>
    private async Task ReplaceInstancesAsync(List<FileTreeNode> targets, string original, FileVersion? version, string which)
    {
        EnsureUiContext();
        var assembly = _currentPath;
        if (_vault == null || assembly == null) return;
        var latestHere = FileAt(original)?.LatestLocalVersion;
        await RunAsync(version == null ? "Returning to the latest version\u2026" : $"Switching to version {version.Version}\u2026", async progress =>
        {
            var useOriginal = version == null || version.Version == latestHere;
            var newPath = useOriginal ? original : await _vault.CreateVersionCopyAsync(original, version!);
            // Only instances that aren't already on it.
            var names = targets.Where(t => !string.Equals(t.FullPath, newPath, StringComparison.OrdinalIgnoreCase)).Select(t => t.InstanceName).ToList();
            if (names.Count > 0 && !_sw.ReplaceInstances(assembly, names, newPath))
            {
                throw new PdmException($"SolidWorks couldn't switch {which}. Try again, or use Replace Components in SolidWorks.");
            }
            _treeStamp = null;
            var label = useOriginal ? "the latest version" : $"version {version!.Version}";
            var keep = FileAt(assembly)?.State == FileState.CheckedOutByMe
                ? " Save and check in the assembly to keep it."
                : " The assembly isn't checked out, so this is just for viewing; check it out to save it.";
            return $"{char.ToUpper(which[0])}{which.Substring(1)} now {(names.Count == 1 ? "uses" : "use")} {label}.{keep}";
        });
    }

    /// <summary>
    /// Switching versions closes and reopens the documents that use the file. Read-only ones (not
    /// checked out) that SolidWorks marks as changed, often just from a rebuild, can't be saved anyway:
    /// ask before closing them without saving. Checked-out documents with unsaved work still stop it.
    /// </summary>
    private bool ConfirmDiscardReadOnly(string path)
    {
        var unsaved = _sw.UnsavedReadOnlyDocumentsUsing(new[] { path });
        if (unsaved.Count == 0) return true;
        return MessageBox.Show(_sw.MainWindow,
            $"{PathList.Format(unsaved.Select(Path.GetFileName))} {(unsaved.Count == 1 ? "is" : "are")} marked as changed, but {(unsaved.Count == 1 ? "isn't" : "aren't")} checked out, " +
            "so the changes can't be saved anyway (often it's just a rebuild).\n\nClose without saving and reopen with the other version?",
            "Jarvis", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
    }

    private async Task ReturnToLatestAsync(string path)
    {
        EnsureUiContext();
        if (_vault == null || !ConfirmDiscardReadOnly(path)) return;
        await RunAsync("Returning to the latest version\u2026", async progress =>
        {
            var closed = _sw.CloseDocumentsUsing(new[] { path }, CloseMode.DiscardReadOnly);
            try
            {
                return Report(await _vault.ReturnToLatestAsync(new[] { path }, progress), "Back to the latest version of");
            }
            finally
            {
                _treeStamp = null;
                _sw.Reopen(closed);
            }
        });
    }

    /// <summary>Distinct vault files in the files-in-use tree (just the active document if there's no tree).</summary>
    private List<VaultFile> TreeFiles()
    {
        var paths = _tree.Root?.DistinctFiles().ToList() ?? (_currentPath != null ? new List<string> { _currentPath } : new List<string>());
        return paths.Select(FileAt).Where(f => f != null).Select(f => f!).ToList();
    }

    private static bool CanCheckOutFile(VaultFile f) => f.IsLockable && f.PinnedVersion == null && !f.IsVersionCopy && f.State is FileState.Available or FileState.ModifiedWithoutCheckOut;

    private static bool CanCheckInFile(VaultFile f) => f.State is FileState.CheckedOutByMe or FileState.NotInVault or FileState.Modified;

    private static bool CanUndoFile(VaultFile f) => f.State is FileState.CheckedOutByMe or FileState.ModifiedWithoutCheckOut or FileState.Modified;

    /// <summary>Ticked files in the tree that the command can act on.</summary>
    private List<string> Ticked(Func<VaultFile, bool> can) => _tree.TickedFiles.Where(p => FileAt(p) is VaultFile f && can(f)).ToList();

    private static string ActionLabel(string verb, int ticked, int available, string what) =>
        ticked > 0 ? $"{verb} ({ticked} ticked)" : available > 1 ? $"{verb}\u2026 ({available} {what})" : verb;

    /// <summary>
    /// Picker rows for the files in the tree a command can act on: the active document first, then
    /// the rest by name. Ticked files (or, with none ticked, the active document) start ticked.
    /// </summary>
    private List<PickItem> TreePickItems(IEnumerable<string> paths, Func<VaultFile, bool> can, Func<VaultFile, string> describe, Func<VaultFile, bool>? preTick = null)
    {
        var top = _currentPath;
        var ticked = new HashSet<string>(Ticked(can), StringComparer.OrdinalIgnoreCase);
        var items = new List<PickItem>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(p => string.Equals(p, top, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var file = FileAt(path);
            if (file == null) continue;
            var isTop = string.Equals(path, top, StringComparison.OrdinalIgnoreCase);
            var allowed = can(file);
            var check = allowed && (ticked.Count > 0 ? ticked.Contains(path) : isTop || preTick?.Invoke(file) == true);
            items.Add(new PickItem(path, Path.GetFileName(path) + (isTop ? "  (this document)" : "") + describe(file), check, !allowed));
        }
        return items;
    }

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

        // Fresh lock info first, so the list shows who has what right now.
        SetStatus("Checking who has what\u2026");
        await RefreshAsync(contactVault: true);
        if (_vault == null) return;

        // Every lockable file in the tree, plus the active document's drawings (changing a part usually means updating its drawing).
        var drawings = (FileAt(top)?.Drawings ?? Array.Empty<string>()).Select(_vault.ToFullPath);
        var paths = TreeFiles().Where(f => f.IsLockable && f.State != FileState.NotInVault).Select(f => _vault.ToFullPath(f.RelativePath)).Concat(drawings).ToList();
        var items = TreePickItems(paths, CanCheckOutFile, f => f.State switch
        {
            FileState.CheckedOutByOther => $"  (checked out by {f.CheckedOutBy})",
            FileState.CheckedOutByMe => "  (already checked out to you)",
            FileState.Frozen => "  (frozen)",
            FileState.InReview => $"  (in review, submitted by {f.ReviewSubmittedBy})",
            _ when f.ChangedSinceDrawing.Count > 0 => "  (drawing, may be out of date)",
            _ => f.IsOutdated ? "  (newer version in vault)" : "",
        });
        if (!items.Any(i => !i.IsLocked))
        {
            SetStatus("Nothing in this document can be checked out right now.");
            return;
        }

        List<string> targets;
        var tickedPaths = _tree.TickedFiles;
        if (tickedPaths.Count > 0)
        {
            targets = Ticked(CanCheckOutFile);
            var skipped = tickedPaths.Except(targets, StringComparer.OrdinalIgnoreCase).Select(p => (Path: p, File: FileAt(p))).Where(x => x.File != null).ToList();
            if (skipped.Count > 0)
            {
                var lines = string.Join("\n", skipped.Select(x => "\u2022 " + Path.GetFileName(x.Path) + x.File!.State switch
                {
                    FileState.CheckedOutByOther => $": checked out by {x.File.CheckedOutBy}",
                    FileState.CheckedOutByMe => ": already checked out to you",
                    FileState.Frozen => ": frozen",
                    FileState.InReview => ": in review",
                    _ => ": can't be checked out",
                }));
                var question = targets.Count > 0 ? $"\n\nCheck out the other {targets.Count} file(s)?" : "";
                var answer = MessageBox.Show(_sw.MainWindow, $"These ticked files can't be checked out:\n{lines}{question}", "Check Out",
                    targets.Count > 0 ? MessageBoxButtons.OKCancel : MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (targets.Count == 0 || answer != DialogResult.OK) return;
            }
        }
        else if (items.Count == 1)
        {
            targets = new List<string> { items[0].Path };
        }
        else
        {
            var taken = items.Count(i => i.IsLocked && FileAt(i.Path)?.State == FileState.CheckedOutByOther);
            using var picker = new FilePickerForm("Check Out",
                "Tick the files to check out." + (taken > 0 ? $" {taken} file(s) are checked out by someone else and can't be ticked." : ""),
                items, "Check Out", askComment: false, askKeep: false);
            if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
            targets = picker.SelectedPaths;
        }

        await RunAsync("Checking out\u2026", async progress =>
        {
            // Pull the newest versions first, so nobody edits an old copy.
            if ((_snapshot?.UpdatesAvailable ?? 0) > 0)
            {
                progress.Report("Getting latest first\u2026");
                if (await GetLatestCoreAsync(progress) == null) return "Check Out cancelled.";
            }
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
            if (result.Failures.Count == 0) _tree.ClearTicks();
            return Report(result, "Checked out");
        });
    }

    private async Task CheckInAsync()
    {
        var top = _currentPath;
        if (top == null || _vault == null) return;

        // Everything in the tree you can check in; new references must go in too, or teammates get missing parts.
        var candidates = TreeFiles().Where(CanCheckInFile).Select(f => _vault.ToFullPath(f.RelativePath)).ToList();
        var newFiles = TreeFiles().Where(f => f.State == FileState.NotInVault).Select(f => _vault.ToFullPath(f.RelativePath)).ToList();
        foreach (var path in _sw.GetReferences(top).Where(IsInVault).Where(p => FileAt(p)?.State == FileState.NotInVault)) newFiles.Add(path);
        candidates.AddRange(newFiles);
        if (candidates.Count == 0)
        {
            SetStatus("Nothing in this document is checked out to you.");
            return;
        }
        var items = TreePickItems(candidates, CanCheckInFile,
            f => f.State == FileState.NotInVault ? "  (new)" : f.HasLocalChanges ? "  (edited)" : "  (checked out, unchanged)",
            preTick: f => f.State == FileState.NotInVault || f.HasLocalChanges);
        var newReferences = items.Count(i => FileAt(i.Path)?.State == FileState.NotInVault);

        var note = newReferences > 0
            ? $"{newReferences} referenced file(s) are new. Check them in too, or teammates will get missing-reference errors."
            : null;
        var tickedForCheckIn = Ticked(CanCheckInFile);
        if (tickedForCheckIn.Count > 0)
        {
            items = tickedForCheckIn.Select(p => new PickItem(p, Path.GetFileName(p), true)).ToList();
            note = null;
        }
        using var picker = new FilePickerForm("Check In",
            tickedForCheckIn.Count > 0
                ? $"Check in {PathList.Format(tickedForCheckIn.Select(Path.GetFileName))}. Unsaved changes are saved first."
                : "Unsaved changes in these files are saved first, then they're checked in.",
            items, "Check In", askComment: true, askKeep: true, note: note, showList: tickedForCheckIn.Count == 0);
        if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;

        var targets = picker.SelectedPaths;
        var comment = picker.Comment;
        var keep = picker.KeepCheckedOut;

        // Version copies the assembly's instances use must go in with it, or teammates get missing parts.
        if (targets.Any(p => Path.GetExtension(p).Equals(".sldasm", StringComparison.OrdinalIgnoreCase)))
        {
            var copies = TreeFiles().Where(f => f.IsVersionCopy && f.State == FileState.NotInVault).Select(f => _vault.ToFullPath(f.RelativePath));
            targets = targets.Union(copies, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // First check-in of a SolidWorks file: ask what it is.
        var newDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newSolidWorks = targets.Where(p => FileAt(p)?.State == FileState.NotInVault && IsSolidWorksFile(p) && !VersionCopies.IsVersionCopy(p)).ToList();
        if (newSolidWorks.Count > 0)
        {
            using var describe = new DescriptionForm(newSolidWorks.Select(p => (p, CurrentDescription(p))).ToList());
            if (describe.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
            newDescriptions = describe.Descriptions;
        }


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
                    "Jarvis — Wrong SolidWorks version", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var exportWarning = await _vault.GetExportWarningAsync(targets);
        if (exportWarning != null &&
            MessageBox.Show(_sw.MainWindow, exportWarning, "Jarvis \u2014 Check in exports?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        await RunAsync("Checking in…", async progress =>
        {
            foreach (var pair in newDescriptions.Where(d => d.Value.Length > 0 && d.Value != CurrentDescription(d.Key)))
            {
                progress.Report($"Saving the description of {Path.GetFileName(pair.Key)}\u2026");
                _sw.SetDescription(pair.Key, pair.Value);
            }
            progress.Report("Saving and closing files…");
            // Properties can only be read while documents are open; references and saved-in version come from disk after saving.
            var properties = targets.Where(IsSolidWorksFile).ToDictionary(
                p => p,
                p => WithDescription(p, _sw.FindOpen(p) is { } doc ? _sw.ReadProperties(doc) : FileAt(p)?.Index?.Properties, newDescriptions),
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
            _sw.SaveTargets(targets);
            var index = properties.ToDictionary(p => p.Key, p => (IndexEntry?)_sw.BuildIndexEntry(p.Key, _vault.VaultRoot, p.Value), StringComparer.OrdinalIgnoreCase);
            var checkedIn = await _vault.CheckInAsync(targets, comment, keep, progress, indexUpdates: index, previews: previews);
            // Released files are read-only on disk again; show them read-only in SolidWorks too.
            if (!keep) _sw.MakeReadOnly(checkedIn.Succeeded.Select(_vault.ToFullPath));
            if (checkedIn.Failures.Count == 0) _tree.ClearTicks();
            return Report(checkedIn, "Checked in");
        });
    }

    /// <summary>The Description property of a SolidWorks file (from the open document, or the vault index).</summary>
    private string CurrentDescription(string path)
    {
        var properties = _sw.FindOpen(path) is { } doc ? _sw.ReadProperties(doc) : FileAt(path)?.Index?.Properties;
        return properties?.Where(p => IndexEntry.DescriptionNames.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    }

    /// <summary>Puts a description typed at check-in into the index even when the file wasn't open (its properties came from the old index).</summary>
    private static Dictionary<string, string>? WithDescription(string path, Dictionary<string, string>? properties, Dictionary<string, string> descriptions)
    {
        if (!descriptions.TryGetValue(path, out var description) || description.Length == 0) return properties;
        return new Dictionary<string, string>(properties ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            [IndexEntry.DescriptionProperty] = description,
        };
    }

    private async Task UndoCheckOutAsync()
    {
        var top = _currentPath;
        if (top == null || _vault == null) return;

        var mine = TreeFiles().Where(CanUndoFile).Select(f => _vault.ToFullPath(f.RelativePath)).ToList();
        if (mine.Count == 0) return;
        var targets = new List<string> { mine[0] };
        var tickedForUndo = Ticked(CanUndoFile);
        if (tickedForUndo.Count > 0)
        {
            targets = tickedForUndo;
            var answer = MessageBox.Show(_sw.MainWindow,
                $"Undo check-out of {PathList.Format(targets.Select(Path.GetFileName))}?\n\nAny changes will be thrown away and the files reloaded from the vault. " +
                $"A backup copy is kept in {FileUtil.BackupRoot}.",
                "Undo Check Out", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
        }
        else if (mine.Count > 1)
        {
            var items = TreePickItems(mine, CanUndoFile, f => f.HasLocalChanges ? "  (edited)" : "");
            using var picker = new FilePickerForm("Undo Check Out",
                "Your changes to the selected files will be thrown away (a backup copy is kept).", items, "Undo Check Out",
                askComment: false, askKeep: false);
            if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
            targets = picker.SelectedPaths;
        }
        else
        {
            var answer = MessageBox.Show(_sw.MainWindow,
                $"Undo check-out of {Path.GetFileName(mine[0])}?\n\nAny changes will be thrown away and the file will be reloaded from the vault. " +
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
            var (pdfs, revisions, pdfProblems) = await SaveReleasePdfsAsync(targets, reason, progress);
            var result = await _vault.FreezeAsync(targets.Concat(pdfs).ToList(), reason, progress,
                revisions: revisions.ToDictionary(p => _vault.ToRelativePath(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase));
            result.Warnings.AddRange(pdfProblems.Select(p => p + " The drawing was still frozen, without a PDF."));
            _sw.MakeReadOnly(result.Succeeded.Select(_vault.ToFullPath));
            return Report(result, "Froze");
        });
    }

    /// <summary>
    /// Releasing a drawing also saves "&lt;name&gt; Rev X.pdf" next to it and checks it in, so it can be
    /// frozen with the same revision. Returns the PDFs, the revision for each drawing and PDF, and problems.
    /// </summary>
    private async Task<(List<string> Pdfs, Dictionary<string, string> Revisions, List<string> Problems)> SaveReleasePdfsAsync(
        IReadOnlyList<string> targets, string reason, IProgress<string> progress)
    {
        var drawings = targets.Where(p => Path.GetExtension(p).Equals(".slddrw", StringComparison.OrdinalIgnoreCase)).ToList();
        var revisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pdfs = new List<string>();
        var problems = new List<string>();
        if (drawings.Count == 0 || _vault == null) return (pdfs, revisions, problems);

        var next = await _vault.GetNextRevisionsAsync(drawings);
        foreach (var drawing in drawings)
        {
            var revision = next[_vault.ToRelativePath(drawing)];
            var pdf = Path.Combine(Path.GetDirectoryName(drawing)!, $"{Path.GetFileNameWithoutExtension(drawing)} Rev {revision}.pdf");
            progress.Report($"Saving {Path.GetFileName(pdf)}\u2026");
            try
            {
                _sw.ExportPdf(drawing, pdf);
                revisions[drawing] = revision;
                revisions[pdf] = revision;
                pdfs.Add(pdf);
            }
            catch (PdmException ex)
            {
                problems.Add(ex.Message);
            }
        }
        if (pdfs.Count > 0)
        {
            progress.Report("Checking in the PDFs\u2026");
            await _vault.CheckInAsync(pdfs, $"Drawing PDF for release: {reason}", progress: progress);
        }
        return (pdfs, revisions, problems);
    }

    /// <summary>Saves a flattened, parts-only bill of materials of the open assembly as a CSV file for Excel.</summary>
    private void ExportBom()
    {
        var top = _currentPath;
        var doc = top != null ? _sw.FindOpen(top) : null;
        if (top == null || doc == null) return;

        List<BomLine> lines;
        try
        {
            lines = _sw.GetBom(doc);
        }
        catch (Exception ex)
        {
            AddinLog.Write("BOM export failed: " + ex);
            SetStatus("Couldn't read the assembly's parts: " + ex.Message);
            return;
        }
        if (lines.Count == 0)
        {
            SetStatus("This assembly has no parts to list (suppressed and excluded-from-BOM parts are left out).");
            return;
        }
        foreach (var line in lines)
        {
            // Lightweight parts aren't loaded: use what the vault index recorded instead.
            var file = FileAt(line.Path);
            if (line.Properties.Count == 0 && file?.Index != null) line.Properties = new Dictionary<string, string>(file.Index.Properties, StringComparer.OrdinalIgnoreCase);
            line.Revision = file?.Revision;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Export BOM",
            Filter = "CSV for Excel (*.csv)|*.csv",
            FileName = Path.GetFileNameWithoutExtension(top) + " BOM.csv",
            // Not in the vault folder: an export there would show up as a new file to check in.
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, Bom.ToCsv(lines), new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Couldn't save the BOM: " + ex.Message);
            return;
        }
        var quantity = lines.Sum(l => l.Quantity);
        SetStatus($"Saved the BOM: {lines.Count} different part(s), {quantity} in total.");
        if (MessageBox.Show(_sw.MainWindow, $"Saved {Path.GetFileName(dialog.FileName)} ({lines.Count} different parts, {quantity} in total). Open it now?",
                "Jarvis", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(dialog.FileName);
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't open it: " + ex.Message);
            }
        }
    }

    private async Task SubmitReviewAsync()
    {
        var top = _currentPath;
        if (top == null || _vault == null) return;

        // Releasing an assembly usually means its parts too: offer them, unticked.
        var items = new List<PickItem> { new PickItem(top, Path.GetFileName(top) + "  (this document)", true) };
        foreach (var path in _sw.GetReferences(top).Where(IsInVault).OrderBy(Path.GetFileName))
        {
            var file = FileAt(path);
            if (file == null || !file.IsLockable) continue;
            var canSubmit = !file.HasLocalChanges && file.State is FileState.Available or FileState.CheckedOutByMe;
            var label = Path.GetFileName(path) + (file.State switch
            {
                FileState.Frozen => "  (frozen)",
                FileState.InReview => "  (already in review)",
                FileState.CheckedOutByOther => $"  (checked out by {file.CheckedOutBy})",
                _ when file.HasLocalChanges => "  (has changes, check in first)",
                _ => "",
            });
            items.Add(new PickItem(path, label, false, !canSubmit));
        }

        using var picker = new FilePickerForm("Submit for review",
            "Nobody can change these files while they're in review. An admin approves (releases) or rejects them.", items, "Submit",
            askComment: true, askKeep: false, commentLabel: "Note to the reviewer (required): what should they check?", defaultComment: "Ready for release");
        if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
        var targets = picker.SelectedPaths;
        var note = picker.Comment;
        await RunAsync("Submitting for review\u2026", async progress =>
        {
            var result = await _vault.SubmitForReviewAsync(targets, note, progress);
            _sw.MakeReadOnly(result.Succeeded.Select(_vault.ToFullPath));
            return Report(result, "Submitted for review:");
        });
    }

    private async Task WithdrawReviewAsync()
    {
        var top = _currentPath;
        if (top == null || _vault == null) return;
        if (MessageBox.Show(_sw.MainWindow, $"Take {Path.GetFileName(top)} out of review? It can then be edited again.", "Jarvis",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await RunAsync("Withdrawing\u2026", async progress => Report(await _vault.WithdrawReviewAsync(new[] { top }, progress), "Withdrew"));
    }

    private async Task ApproveReviewAsync()
    {
        var top = _currentPath;
        if (top == null || _vault == null) return;
        var items = new List<PickItem> { new PickItem(top, Path.GetFileName(top) + "  (this document)", true) };
        // Parts submitted along with this assembly can be approved together.
        foreach (var path in _sw.GetReferences(top).Where(IsInVault).Where(p => FileAt(p)?.State == FileState.InReview).OrderBy(Path.GetFileName))
        {
            items.Add(new PickItem(path, $"{Path.GetFileName(path)}  (submitted by {FileAt(path)!.ReviewSubmittedBy})", true));
        }

        using var picker = new FilePickerForm("Approve & release (admin)",
            "Approving freezes these files with the next revision letter. Drawings also get a PDF saved and frozen with them.", items, "Approve",
            askComment: true, askKeep: false, commentLabel: "Release note (required):", defaultComment: "Approved for release");
        if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
        var targets = picker.SelectedPaths;
        var reason = picker.Comment;

        await RunAsync("Approving\u2026", async progress =>
        {
            var (pdfs, revisions, pdfProblems) = await SaveReleasePdfsAsync(targets, reason, progress);
            var relativeRevisions = revisions.ToDictionary(p => _vault.ToRelativePath(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
            var result = await _vault.ApproveReviewAsync(targets, reason, progress, revisions: relativeRevisions);
            if (pdfs.Count > 0)
            {
                var frozenPdfs = await _vault.FreezeAsync(pdfs, reason, progress, revisions: relativeRevisions);
                result.Succeeded.AddRange(frozenPdfs.Succeeded);
                result.Failures.AddRange(frozenPdfs.Failures);
            }
            result.Warnings.AddRange(pdfProblems.Select(p => p + " The drawing was still released, without a PDF."));
            _sw.MakeReadOnly(result.Succeeded.Select(_vault.ToFullPath));
            return Report(result, "Approved and released");
        });
    }

    private async Task RejectReviewAsync()
    {
        var top = _currentPath;
        var file = CurrentFile;
        if (top == null || _vault == null || file == null) return;
        using var picker = new FilePickerForm("Reject (admin)",
            $"{file.ReviewSubmittedBy} will be told why, and the file can be edited again.",
            new[] { new PickItem(top, Path.GetFileName(top), true, isLocked: false) }, "Reject",
            askComment: true, askKeep: false, commentLabel: "Reason (required): what needs to change?", defaultComment: "");
        if (picker.ShowDialog(_sw.MainWindow) != DialogResult.OK) return;
        var reason = picker.Comment;
        await RunAsync("Rejecting\u2026", async progress => Report(await _vault.RejectReviewAsync(new[] { top }, reason, progress), "Rejected"));
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
            $"{file.CheckedOutBy}'s PC will check in their last saved version of this file and release it, the next time Jarvis or SolidWorks " +
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
                                       "that use it in Jarvis and click Check In to finish.", ex.Detail, ex);
            }
            finally
            {
                _sw.Reopen(reopen);
            }
        });
    }

    /// <summary>
    /// With "only download my folders", an assembly can use parts that are only placeholders here. Download
    /// them, then reopen the assembly so SolidWorks loads them.
    /// </summary>
    private void ToggleWatch()
    {
        var file = CurrentFile;
        if (file == null || _vault == null) return;
        if (WatchList.IsWatching(_vault.VaultRoot, file.RelativePath))
        {
            WatchList.Stop(_vault.VaultRoot, file.RelativePath);
            SetStatus($"You won't be told about {file.Name}.");
        }
        else
        {
            WatchList.Watch(_vault.VaultRoot, file.RelativePath);
            SetStatus($"You'll be told when {file.Name} is free.");
        }
        UpdateView();
    }

    private async Task DownloadMissingPartsAsync()
    {
        var top = _currentPath;
        var missing = _missingRefs.ToList();
        if (top == null || _vault == null || missing.Count == 0) return;

        await RunAsync($"Downloading {missing.Count} part(s)\u2026", async progress =>
        {
            await _vault.DownloadAsync(missing, progress);
            var closed = _sw.CloseIfSaved(new[] { top });
            _sw.Reopen(closed);
            return closed.Paths.Count > 0
                ? $"Downloaded {PathList.Format(missing)} and reopened {Path.GetFileName(top)}."
                : $"Downloaded {PathList.Format(missing)}. Save and reopen {Path.GetFileName(top)} to load them.";
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

        await RunAsync("Getting latest\u2026", async progress => await GetLatestCoreAsync(progress) ?? "Get Latest cancelled.");
    }

    /// <summary>Gets the latest versions, offering to close and reopen open files that changed. Null if the user cancels.</summary>
    private async Task<string?> GetLatestCoreAsync(IProgress<string> progress)
    {
        if (_vault == null) return null;
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
                if (answer != DialogResult.OK) return null;

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
        }
    }

    private void OpenDesktopApp()
    {
        var exe = Path.Combine(Jarvis.Core.Config.AppFolders.InstallFolder, "Jarvis.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show(_sw.MainWindow, $"Jarvis isn't installed at {exe}. Run Setup.cmd.", "Jarvis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    // ------------------------------------------------------------ plumbing

    private async Task RunAsync(string busyText, Func<IProgress<string>, Task<string>> operation)
    {
        EnsureUiContext();
        if (_busy || _vault == null) return;
        _busy = true;
        StartActivity(busyText);
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
            MessageBox.Show(_sw.MainWindow, ex.Message, "Jarvis — " + ex.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            status = "Error";
            AddinLog.Write("Command failed: " + ex);
            MessageBox.Show(_sw.MainWindow,
                "Something unexpected went wrong: " + ex.Message + $"\n\nDetails were written to {AddinLog.FilePath}.",
                "Jarvis", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            StopActivity();
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
            MessageBox.Show(_sw.MainWindow, header + string.Join("\n\n", sections), "Jarvis", MessageBoxButtons.OK,
                result.Failures.Count > 0 || result.Warnings.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        return status;
    }
}
