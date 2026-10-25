using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Jarvis.Core.Vault;

namespace Jarvis.SwAddin;

/// <summary>
/// Shown when files are checked into the vault for the first time: one Description box per new SolidWorks
/// file. When the vault has a part-numbering convention, each file also gets a group: Jarvis gives it the next
/// free number (saved in its PartNumber property) and offers to rename the file to match.
/// </summary>
internal sealed class DescriptionForm : Form
{
    private sealed class Row
    {
        public string Path = "";
        public TextBox Description = null!;
        public ComboBox? Group;
        public Label? Number;
        public CheckBox? Rename;
        public int? AssignedNumber;
    }

    private readonly List<Row> _rowsData = new List<Row>();
    private readonly NumberingScheme? _scheme;
    private readonly IReadOnlyList<string> _used;
    private readonly TableLayoutPanel _rows = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
    private readonly Panel _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Label _intro = new Label { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
    private const string NoNumber = "(no number)";

    /// <param name="files">New files with their current Description (may be empty).</param>
    /// <param name="scheme">The vault's part-numbering convention, or null.</param>
    /// <param name="used">Part numbers and file names already in the vault.</param>
    public DescriptionForm(IReadOnlyList<(string Path, string Description)> files, NumberingScheme? scheme = null, IReadOnlyList<string>? used = null)
    {
        _scheme = scheme;
        _used = used ?? Array.Empty<string>();
        Text = scheme == null ? "Describe new files" : "Describe and number new files";
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        // Sized by hand in OnLoad (WinForms auto-scaling misbehaves inside SolidWorks).
        AutoScaleMode = AutoScaleMode.None;

        _intro.Text = (files.Count == 1
                          ? "This file is new to the vault. Describe it so teammates know what it is (e.g. \"Motor mount bracket, 1/4in aluminum\")."
                          : $"These {files.Count} files are new to the vault. Describe each one so teammates know what it is.") +
                      (scheme == null ? "" : " Pick its group to give it the team's next part number.");

        _rows.ColumnCount = scheme == null ? 2 : 3;
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (scheme != null) _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (path, description) in files)
        {
            var row = new Row { Path = path };
            _rows.Controls.Add(new Label { Text = System.IO.Path.GetFileName(path), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) });
            if (scheme != null)
            {
                row.Group = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Margin = new Padding(0, 3, 8, 3) };
                row.Group.Items.Add(NoNumber);
                foreach (var group in scheme.Groups.OrderBy(g => g.Number)) row.Group.Items.Add(group);
                // A file already named by the convention keeps its number.
                var existing = scheme.NumberIn(path) is { } n ? scheme.GroupOf(n) : null;
                row.Group.SelectedItem = (object?)existing ?? NoNumber;
                row.Group.SelectedIndexChanged += (_, _) => Renumber();
                _rows.Controls.Add(row.Group);
            }
            row.Description = new TextBox { Text = description, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 0, 3) };
            row.Description.TextChanged += (_, _) => Renumber();
            _rows.Controls.Add(row.Description);
            if (scheme != null)
            {
                // Second line: the number it gets, and renaming the file to match.
                row.Number = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 10, 8) };
                row.Rename = new CheckBox { AutoSize = true, Checked = true, Margin = new Padding(0, 0, 0, 8) };
                _rows.Controls.Add(row.Number);
                _rows.SetColumnSpan(row.Number, 2);
                _rows.Controls.Add(row.Rename);
            }
            _rowsData.Add(row);
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
        Shown += (_, _) => (_rowsData.Select(r => r.Description).FirstOrDefault(b => b.Text.Length == 0) ?? _rowsData.FirstOrDefault()?.Description)?.Focus();
        Renumber();
    }

    /// <summary>The descriptions as typed, by file path.</summary>
    public Dictionary<string, string> Descriptions =>
        _rowsData.ToDictionary(r => r.Path, r => r.Description.Text.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Part numbers given to files (e.g. "TLT-203"), by file path.</summary>
    public Dictionary<string, string> PartNumbers =>
        _rowsData.Where(r => r.AssignedNumber != null && _scheme != null)
            .ToDictionary(r => r.Path, r => _scheme!.Format(r.AssignedNumber!.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>Files to rename after check-in, by path: the new file name (with extension).</summary>
    public Dictionary<string, string> Renames =>
        _rowsData.Where(r => r.AssignedNumber != null && r.Rename is { Visible: true, Checked: true } && _scheme != null)
            .ToDictionary(r => r.Path, r => NewName(r)!, StringComparer.OrdinalIgnoreCase);

    private string? NewName(Row row) =>
        row.AssignedNumber is int n && _scheme != null
            ? _scheme.FileName(n, row.Description.Text.Trim().Length > 0 ? row.Description.Text.Trim() : System.IO.Path.GetFileNameWithoutExtension(row.Path)) + System.IO.Path.GetExtension(row.Path)
            : null;

    /// <summary>Gives each file with a group the next free number, in order, and shows what it gets.</summary>
    private void Renumber()
    {
        if (_scheme == null) return;
        var used = new List<string>(_used);
        foreach (var row in _rowsData)
        {
            row.AssignedNumber = null;
            if (row.Group?.SelectedItem is not NumberGroup group)
            {
                row.Number!.Text = "No part number";
                row.Rename!.Visible = false;
                continue;
            }
            var own = _scheme.NumberIn(row.Path);
            try
            {
                row.AssignedNumber = own != null && _scheme.GroupOf(own.Value) == group ? own : _scheme.NextNumber(group, used);
            }
            catch (Core.Errors.PdmException ex)
            {
                row.Number!.Text = ex.Message;
                row.Rename!.Visible = false;
                continue;
            }
            used.Add(_scheme.Format(row.AssignedNumber!.Value));
            row.Number!.Text = $"Part number {_scheme.Format(row.AssignedNumber.Value)}";
            var newName = NewName(row)!;
            row.Rename!.Visible = !string.Equals(newName, System.IO.Path.GetFileName(row.Path), StringComparison.OrdinalIgnoreCase);
            row.Rename.Text = $"Rename the file to {newName} (after check-in)";
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        float scale;
        using (var g = CreateGraphics()) scale = g.DpiX / 96f;
        int Scaled(int value) => (int)Math.Round(value * scale);
        MinimumSize = new Size(Scaled(460), Scaled(220));
        var perRow = _scheme == null ? 34 : 62;
        Size = new Size(Scaled(_scheme == null ? 620 : 760), Scaled(Math.Min(600, 190 + _rowsData.Count * perRow)));
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
