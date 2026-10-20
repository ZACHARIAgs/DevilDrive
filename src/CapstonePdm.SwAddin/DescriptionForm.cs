using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CapstonePdm.SwAddin;

/// <summary>
/// Shown when files are checked into the vault for the first time: one Description box per new
/// SolidWorks file. The descriptions are saved into the files' Description property before check-in.
/// </summary>
internal sealed class DescriptionForm : Form
{
    private readonly Dictionary<string, TextBox> _boxes = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
    private readonly TableLayoutPanel _rows = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
    private readonly Panel _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Label _intro = new Label { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };

    /// <param name="files">New files with their current Description (may be empty).</param>
    public DescriptionForm(IReadOnlyList<(string Path, string Description)> files)
    {
        Text = "Describe new files";
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        // Sized by hand in OnLoad (WinForms auto-scaling misbehaves inside SolidWorks).
        AutoScaleMode = AutoScaleMode.None;

        _intro.Text = files.Count == 1
            ? "This file is new to the vault. Describe it so teammates know what it is (e.g. \"Motor mount bracket, 1/4in aluminum\")."
            : $"These {files.Count} files are new to the vault. Describe each one so teammates know what it is.";

        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (path, description) in files)
        {
            var label = new Label { Text = Path.GetFileName(path), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) };
            var box = new TextBox { Text = description, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 0, 3) };
            _boxes[path] = box;
            _rows.Controls.Add(label);
            _rows.Controls.Add(box);
        }
        _scroll.Controls.Add(_rows);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
        var ok = new Button { Text = "Check In", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var layout = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        layout.Controls.Add(_scroll);
        layout.Controls.Add(buttons);
        layout.Controls.Add(_intro);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => (_boxes.Values.FirstOrDefault(b => b.Text.Length == 0) ?? _boxes.Values.FirstOrDefault())?.Focus();
    }

    /// <summary>The descriptions as typed, by file path.</summary>
    public Dictionary<string, string> Descriptions =>
        _boxes.ToDictionary(b => b.Key, b => b.Value.Text.Trim(), StringComparer.OrdinalIgnoreCase);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        float scale;
        using (var g = CreateGraphics()) scale = g.DpiX / 96f;
        int Scaled(int value) => (int)Math.Round(value * scale);
        MinimumSize = new Size(Scaled(460), Scaled(220));
        Size = new Size(Scaled(620), Scaled(Math.Min(560, 190 + _boxes.Count * 34)));
        FitWidth();
        Resize += (_, _) => FitWidth();
        if (Owner == null) CenterToScreen();
    }

    private void FitWidth()
    {
        var width = Math.Max(100, _scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        _rows.Width = width;
        _intro.MaximumSize = new Size(Math.Max(100, ClientSize.Width - 24), 0);
    }
}
