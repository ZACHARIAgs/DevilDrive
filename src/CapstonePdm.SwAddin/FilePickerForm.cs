using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CapstonePdm.SwAddin;

internal sealed class PickItem
{
    public PickItem(string path, string label, bool isChecked, bool isLocked = false)
    {
        Path = path;
        Label = label;
        IsChecked = isChecked;
        IsLocked = isLocked;
    }

    public string Path { get; }
    public string Label { get; }
    public bool IsChecked { get; }
    /// <summary>Shown but can't be selected (e.g. checked out by someone else).</summary>
    public bool IsLocked { get; }

    public override string ToString() => Label;
}

/// <summary>Choose files (an assembly and its references), optionally with a check-in comment.</summary>
internal sealed class FilePickerForm : Form
{
    private readonly CheckedListBox _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly TextBox _comment = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 70 };
    private readonly CheckBox _keep = new CheckBox { AutoSize = true, Text = "Keep checked out (save a version and keep editing)" };
    private readonly Button _ok = new Button { DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 28) };
    private readonly TableLayoutPanel _layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
    private readonly bool _askComment;
    private readonly bool _showList;

    /// <param name="showList">False when the files were already chosen (ticked in the pane): only the comment is asked for.</param>
    public FilePickerForm(string title, string intro, IReadOnlyList<PickItem> items, string okText, bool askComment, bool askKeep, string? note = null,
        string commentLabel = "Comment (required): what changed and why", string defaultComment = "", bool showList = true)
    {
        _showList = showList;
        _comment.Text = defaultComment;
        _askComment = askComment;
        Text = title;
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        // Sized by hand in OnLoad for the screen's DPI. WinForms auto-scaling inside the
        // SolidWorks process shrank the window and hid the comment box and buttons.
        AutoScaleMode = AutoScaleMode.None;

        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++) _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var layout = _layout;
        layout.Controls.Add(new Label { Text = intro, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        layout.Controls.Add(_list);
        _list.Visible = showList;

        foreach (var item in items) _list.Items.Add(item, item.IsChecked && !item.IsLocked);
        _list.ItemCheck += (_, e) =>
        {
            if (((PickItem)_list.Items[e.Index]).IsLocked && e.NewValue == CheckState.Checked) e.NewValue = CheckState.Unchecked;
            // ItemCheck fires before the change lands; re-check afterwards.
            if (IsHandleCreated) BeginInvoke(new Action(UpdateOk));
        };

        var selectAll = new LinkLabel { Text = "Select all", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        selectAll.LinkClicked += (_, _) =>
        {
            for (var i = 0; i < _list.Items.Count; i++)
            {
                if (!((PickItem)_list.Items[i]).IsLocked) _list.SetItemChecked(i, true);
            }
        };
        if (showList) layout.Controls.Add(selectAll);

        if (note != null)
        {
            layout.Controls.Add(new Label { Text = note, AutoSize = true, ForeColor = Color.FromArgb(0xB2, 0x4A, 0x00), Margin = new Padding(0, 6, 0, 0) });
        }

        if (askComment)
        {
            layout.Controls.Add(new Label { Text = commentLabel, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) });
            layout.Controls.Add(_comment);
            _comment.TextChanged += (_, _) => UpdateOk();
        }
        if (askKeep) layout.Controls.Add(_keep);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        _ok.Text = okText;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = askComment ? null : _ok;
        CancelButton = cancel;
        Shown += (_, _) => (askComment ? (Control)_comment : _list).Focus();
        UpdateOk();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        float scale;
        using (var g = CreateGraphics()) scale = g.DpiX / 96f;
        int Scaled(int value) => (int)Math.Round(value * scale);

        MinimumSize = new Size(Scaled(420), Scaled(320));
        Size = new Size(Scaled(560), Scaled(!_showList ? 300 : _askComment ? 520 : 420));
        if (!_showList) MinimumSize = new Size(Scaled(420), Scaled(260));
        _comment.Height = Scaled(70);
        _ok.MinimumSize = new Size(Scaled(90), Scaled(28));
        FitLabels();
        Resize += (_, _) => FitLabels();
        if (Owner == null) CenterToScreen();
    }

    /// <summary>Wraps text labels to the window width.</summary>
    private void FitLabels()
    {
        var width = Math.Max(100, _layout.ClientSize.Width - _layout.Padding.Horizontal);
        foreach (Control c in _layout.Controls)
        {
            if (c is Label label && !(c is LinkLabel)) label.MaximumSize = new Size(width, 0);
        }
    }

    public List<string> SelectedPaths => _list.CheckedItems.Cast<PickItem>().Select(i => i.Path).ToList();
    public string Comment => _comment.Text.Trim();
    public bool KeepCheckedOut => _keep.Checked;

    private void UpdateOk() =>
        _ok.Enabled = _list.CheckedItems.Count > 0 && (!_askComment || !string.IsNullOrWhiteSpace(_comment.Text));
}
