using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Jarvis.Core.Vault;

namespace Jarvis.SwAddin;

/// <summary>Pick which checked-in version of a file to use on this PC.</summary>
internal sealed class VersionPickerForm : Form
{
    private readonly ListView _list = new ListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };
    private readonly Button _ok = new Button { Text = "Use this version", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(120, 28) };

    public VersionPickerForm(string fileName, IReadOnlyList<FileVersion> versions, int currentVersion, int latestVersion, string? explanation = null)
    {
        Text = "Use an older version of " + fileName;
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;

        var intro = new Label
        {
            Text = explanation ?? "Only this PC switches: the vault and your teammates keep the latest version.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 52,
            Padding = new Padding(0, 0, 0, 6),
        };

        _list.Columns.Add("Version", 70);
        _list.Columns.Add("Checked in", 130);
        _list.Columns.Add("By", 110);
        _list.Columns.Add("Comment", 320);
        foreach (var version in versions.OrderByDescending(v => v.Version))
        {
            var label = version.Version.ToString() + (version.Version == latestVersion ? " (latest)" : "") + (version.Version == currentVersion ? " \u2190 now" : "");
            var item = new ListViewItem(new[] { label, version.Date.LocalDateTime.ToString("MMM d, yyyy h:mm tt"), version.Author, version.CommentFirstLine }) { Tag = version };
            _list.Items.Add(item);
        }
        _list.SelectedIndexChanged += (_, _) => _ok.Enabled = SelectedVersion != null && SelectedVersion.Version != currentVersion;
        _list.DoubleClick += (_, _) => { if (_ok.Enabled) DialogResult = DialogResult.OK; };

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);

        var layout = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        layout.Controls.Add(_list);
        layout.Controls.Add(buttons);
        layout.Controls.Add(intro);
        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = cancel;
        _ok.Enabled = false;
    }

    public FileVersion? SelectedVersion => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as FileVersion : null;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        float scale;
        using (var g = CreateGraphics()) scale = g.DpiX / 96f;
        Size = new Size((int)(700 * scale), (int)(420 * scale));
        MinimumSize = new Size((int)(500 * scale), (int)(300 * scale));
        foreach (ColumnHeader column in _list.Columns) column.Width = (int)(column.Width * scale);
        if (Owner == null) CenterToScreen();
    }
}
