using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Jarvis.Core.Vault;

namespace Jarvis.SwAddin;

/// <summary>
/// Import files into the vault: one row per SolidWorks file found, with its group (when the vault has a
/// part-numbering convention), name and description, and the new file name it gets. Plus the vault folder it goes in.
/// </summary>
internal sealed class ImportForm : Form
{
    private const string NoNumber = "(no number)";
    private const string TopOfVault = "(top of the vault)";

    private readonly IReadOnlyList<(string Path, bool FromElsewhere)> _files;
    private readonly NumberingScheme? _scheme;
    private readonly IReadOnlyList<string> _used;
    private readonly Func<string, bool> _existsInVault;
    /// <summary>Per file (same order as <see cref="_files"/>): the vault file with the same name, if there is one.</summary>
    private readonly (string RelativePath, bool Identical)?[] _inVault;
    private readonly DataGridView _grid = new DataGridView
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.CellSelect,
        BackgroundColor = SystemColors.Window, EditMode = DataGridViewEditMode.EditOnEnter,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
    };
    private readonly ComboBox _folder = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 260 };
    private readonly Label _problems = new Label { AutoSize = true, ForeColor = Color.Firebrick, Dock = DockStyle.Bottom, Padding = new Padding(0, 6, 0, 0) };
    private readonly Button _ok = new Button { Text = "Import and Check In", AutoSize = true, MinimumSize = new Size(150, 28) };

    /// <param name="vaultMatch">The vault file with the same name as an imported file, and whether it's identical, or null.</param>
    public ImportForm(IReadOnlyList<(string Path, bool FromElsewhere)> files, string sourceFolder, NumberingScheme? scheme, IReadOnlyList<string> used,
        IReadOnlyList<string> vaultFolders, Func<string, bool> existsInVault, Func<string, (string RelativePath, bool Identical)?> vaultMatch)
    {
        _files = files;
        _scheme = scheme;
        _used = used;
        _existsInVault = existsInVault;
        _inVault = files.Select(f => vaultMatch(f.Path)).ToArray();
        Text = "Import files into the vault";
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.None;

        var intro = new Label
        {
            AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8),
            Text = $"{files.Count} SolidWorks file(s) from {sourceFolder}" +
                   (files.Any(f => f.FromElsewhere) ? $" (including {files.Count(f => f.FromElsewhere)} they use from other folders)" : "") + ". " +
                   "They're copied into the vault under the new names, every assembly and drawing is pointed at the copies, and it's all checked in. " +
                   "The original files aren't changed." + (scheme == null ? "" : " Pick each file's group to give it the next part number.") +
                   (_inVault.Any(m => m != null) ? " Files the vault already has use the vault's file instead of a second copy; untick one to import it under another name." : ""),
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "File", ReadOnly = true, FillWeight = 120 });
        var anyInVault = _inVault.Any(m => m != null);
        if (anyInVault) _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "UseVault", HeaderText = "Use the vault's file", FillWeight = 55 });
        if (scheme != null)
        {
            var group = new DataGridViewComboBoxColumn { Name = "Group", HeaderText = "Group", FillWeight = 80, FlatStyle = FlatStyle.Flat };
            group.Items.Add(NoNumber);
            foreach (var g in scheme.Groups.OrderBy(g => g.Number)) group.Items.Add(g.ToString());
            _grid.Columns.Add(group);
        }
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", FillWeight = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", HeaderText = "Description", FillWeight = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "NewName", HeaderText = "New file name", ReadOnly = true, FillWeight = 130 });

        for (var i = 0; i < files.Count; i++)
        {
            var (path, fromElsewhere) = files[i];
            var row = _grid.Rows[_grid.Rows.Add()];
            var match = _inVault[i];
            row.Cells["File"].Value = Path.GetFileName(path) + (fromElsewhere ? "  (from another folder)" : "") +
                                      (match == null ? "" : match.Value.Identical ? "  (same as the vault's)" : "  (differs from the vault's)");
            row.Cells["File"].ToolTipText = path;
            if (anyInVault)
            {
                if (match == null)
                {
                    row.Cells["UseVault"] = new DataGridViewTextBoxCell { Value = "" };
                    row.Cells["UseVault"].ReadOnly = true;
                }
                else
                {
                    // Ticked by default; untick to import it as a separate file under another name (a different part that happens to share the name).
                    row.Cells["UseVault"].Value = true;
                    row.Cells["UseVault"].ToolTipText = match.Value.Identical
                        ? $"Identical to the vault's {match.Value.RelativePath}. Ticked: the imported files use that one. Unticked: it's imported as a separate file under another name."
                        : $"The vault's {match.Value.RelativePath} has the same name but isn't the same file. Ticked: the imported files use the vault's (this copy's changes aren't imported). Unticked: it's imported under another name.";
                }
            }
            row.Cells["Name"].Value = scheme == null ? Path.GetFileNameWithoutExtension(path) : VaultImport.DescriptiveName(path, scheme);
            if (scheme != null)
            {
                var own = scheme.NumberIn(path) is { } n ? scheme.GroupOf(n) : null;
                row.Cells["Group"].Value = own?.ToString() ?? NoNumber;
            }
        }
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewComboBoxCell or DataGridViewCheckBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name != "NewName") Recalculate();
        };

        _folder.Items.Add(TopOfVault);
        foreach (var folder in vaultFolders) _folder.Items.Add(folder);
        _folder.Text = vaultFolders.FirstOrDefault(f => string.Equals(f, "Sandbox", StringComparison.OrdinalIgnoreCase)) ?? TopOfVault;
        _folder.TextChanged += (_, _) => Recalculate();
        var folderRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
        folderRow.Controls.Add(new Label { Text = "Put them in this vault folder:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        folderRow.Controls.Add(_folder);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        _ok.Click += (_, _) =>
        {
            Recalculate();
            if (_problems.Text.Length == 0) DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);

        var layout = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        layout.Controls.Add(_grid);
        layout.Controls.Add(_problems);
        layout.Controls.Add(buttons);
        layout.Controls.Add(folderRow);
        layout.Controls.Add(intro);
        Controls.Add(layout);
        CancelButton = cancel;
        Load += (_, _) =>
        {
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Size = new Size((int)(980 * scale), (int)(Math.Min(720, 260 + files.Count * 26) * scale));
            intro.MaximumSize = new Size(ClientSize.Width - 30, 0);
            if (Owner == null) CenterToScreen();
        };
        Recalculate();
    }

    /// <summary>The files to import (original and vault-relative target), with the part number and description to give each.</summary>
    public IReadOnlyList<(ImportItem Item, string? PartNumber, string Description)> Result { get; private set; } = Array.Empty<(ImportItem, string?, string)>();

    private string TargetFolder => string.Equals(_folder.Text.Trim(), TopOfVault, StringComparison.Ordinal) ? "" : _folder.Text.Trim().Replace('\\', '/').Trim('/');

    /// <summary>Numbers every row in order, works out the new names and checks them.</summary>
    private void Recalculate()
    {
        var used = new List<string>(_used);
        var result = new List<(ImportItem, string?, string)>();
        var problems = new List<string>();
        for (var i = 0; i < _files.Count; i++)
        {
            var row = _grid.Rows[i];
            var path = _files[i].Path;
            var name = (row.Cells["Name"].Value as string ?? "").Trim();
            var description = (row.Cells["Description"].Value as string ?? "").Trim();
            var useVault = _inVault[i] != null && row.Cells["UseVault"].Value is true;
            foreach (var column in new[] { "Group", "Name", "Description" }.Where(_grid.Columns.Contains)) row.Cells[column].ReadOnly = useVault;
            if (useVault)
            {
                var vaultPath = _inVault[i]!.Value.RelativePath;
                row.Cells["NewName"].Value = "Uses the vault's " + vaultPath;
                result.Add((new ImportItem(path, vaultPath, useExisting: true), null, ""));
                continue;
            }
            string? partNumber = null;
            string fileName;
            var group = _scheme == null ? null : _scheme.Groups.FirstOrDefault(g => g.ToString() == row.Cells["Group"].Value as string);
            if (_scheme != null && group != null)
            {
                try
                {
                    var own = _scheme.NumberIn(path);
                    var number = own != null && _scheme.GroupOf(own.Value) == group && !used.Any(u => _scheme.NumberIn(u) == own) ? own.Value : _scheme.NextNumber(group, used);
                    partNumber = _scheme.Format(number);
                    used.Add(partNumber);
                    fileName = _scheme.FileName(number, name);
                }
                catch (Core.Errors.PdmException ex)
                {
                    problems.Add(ex.Message);
                    fileName = name;
                }
            }
            else
            {
                fileName = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(path);
            }
            fileName += Path.GetExtension(path);
            var target = TargetFolder.Length == 0 ? fileName : TargetFolder + "/" + fileName;
            row.Cells["NewName"].Value = fileName;
            result.Add((new ImportItem(path, target), partNumber, description));
        }
        if (TargetFolder.Length > 0 && VaultService.FolderNameProblem(TargetFolder.Split('/').Last()) is { } folderProblem) problems.Add("Vault folder: " + folderProblem);
        problems.AddRange(VaultImport.Problems(result.Select(r => r.Item1).ToList(), _existsInVault));
        _problems.Text = string.Join(Environment.NewLine, problems.Distinct().Take(4));
        _ok.Enabled = problems.Count == 0 && result.Count > 0;
        Result = result;
    }
}
