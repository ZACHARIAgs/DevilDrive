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
/// free number (saved in its PartNumber property) and offers to rename the file to match. When the file name is
/// just the number (<see cref="NumberingScheme.NameIsNumber"/>), each file is either ours (it gets the next number
/// and is renamed to it, at the top of the vault) or bought (it keeps its name and goes in the COTS folder);
/// drawings are named after their model.
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
    private const string Bought = "COTS (bought; keeps its name)";
    private readonly Func<string, string> _relative;

    /// <param name="files">New files with their current Description (may be empty).</param>
    /// <param name="scheme">The vault's part-numbering convention, or null.</param>
    /// <param name="used">Part numbers and file names already in the vault.</param>
    /// <param name="relative">Vault-relative path of a file (to tell COTS files and McMaster downloads).</param>
    public DescriptionForm(IReadOnlyList<(string Path, string Description)> files, NumberingScheme? scheme = null, IReadOnlyList<string>? used = null,
        Func<string, string>? relative = null)
    {
        _scheme = scheme;
        _used = used ?? Array.Empty<string>();
        _relative = relative ?? (p => System.IO.Path.GetFileName(p));
        var nameIsNumber = scheme?.NameIsNumber == true;
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
                      (scheme == null ? "" : nameIsNumber
                          ? $" Our parts and assemblies are named by the team's next part number ({scheme.Example()}), so the description is what says what each one is. Bought parts keep their names and go in {(scheme.CotsFolder.Length > 0 ? scheme.CotsFolder : "the vault")}."
                          : " Pick its group to give it the team's next part number.");

        _rows.ColumnCount = scheme == null ? 2 : 3;
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (scheme != null) _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (path, description) in files)
        {
            var row = new Row { Path = path };
            _rows.Controls.Add(new Label { Text = System.IO.Path.GetFileName(path), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) });
            if (scheme != null && nameIsNumber && IsDrawing(path))
            {
                // A drawing takes its model's name (TLT-007.SLDDRW), after check-in.
                _rows.Controls.Add(new Label { Text = "Named after its model", AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) });
            }
            else if (scheme != null)
            {
                row.Group = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = nameIsNumber ? 210 : 170, Margin = new Padding(0, 3, 8, 3) };
                if (nameIsNumber)
                {
                    row.Group.Items.Add(scheme.Groups[0]);
                    row.Group.Items.Add(Bought);
                    row.Group.Format += (_, e) => { if (e.ListItem is NumberGroup) e.Value = $"Ours ({scheme.Prefix}{scheme.Separator}{new string('X', scheme.Digits)})"; };
                    row.Group.SelectedItem = scheme.LooksBought(_relative(path)) ? Bought : scheme.Groups[0];
                }
                else
                {
                    row.Group.Items.Add(NoNumber);
                    foreach (var group in scheme.Groups.OrderBy(g => g.Number)) row.Group.Items.Add(group);
                    // A file already named by the convention keeps its number.
                    var existing = scheme.NumberIn(path) is { } n ? scheme.GroupOf(n) : null;
                    row.Group.SelectedItem = (object?)existing ?? NoNumber;
                }
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
        // With file names that are only numbers, the description is the only thing that says what a part is.
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK || !nameIsNumber) return;
            var missing = _rowsData.FirstOrDefault(r => r.AssignedNumber != null && r.Description.Text.Trim().Length == 0);
            if (missing == null) return;
            e.Cancel = true;
            MessageBox.Show(this, $"Describe {System.IO.Path.GetFileName(missing.Path)}: its file name will only be {_scheme!.Format(missing.AssignedNumber!.Value)}, so the description is what tells teammates what it is.",
                "Describe new files", MessageBoxButtons.OK, MessageBoxIcon.Information);
            missing.Description.Focus();
        };
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
        _rowsData.Where(r => r.AssignedNumber != null && _scheme != null && (_scheme.NameIsNumber || r.Rename is { Visible: true, Checked: true }))
            .Where(r => !string.Equals(NewName(r), System.IO.Path.GetFileName(r.Path), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(r => r.Path, r => NewName(r)!, StringComparer.OrdinalIgnoreCase);

    /// <summary>With file names that are numbers: the files marked as bought (COTS), which go in the COTS folder.</summary>
    public HashSet<string> BoughtFiles =>
        new HashSet<string>(_rowsData.Where(r => r.Group?.SelectedItem as string == Bought).Select(r => r.Path), StringComparer.OrdinalIgnoreCase);

    private static bool IsDrawing(string path) => path.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase);

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
                row.Number!.Text = !_scheme.NameIsNumber ? "No part number"
                    : row.Group == null ? "Gets its model's name"
                    : _scheme.IsCots(_relative(row.Path)) || _scheme.CotsFolder.Length == 0 ? "Keeps its name"
                    : $"Keeps its name; goes in {_scheme.CotsFolder} (after check-in)";
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
            if (_scheme.NameIsNumber)
            {
                row.Number.Text = $"Becomes {newName} (part number {_scheme.Format(row.AssignedNumber.Value)}, at the top of the vault, after check-in)";
                row.Rename!.Visible = false;
                continue;
            }
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
