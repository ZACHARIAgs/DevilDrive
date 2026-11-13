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
    private const string Ours = "Ours (gets the next number)";
    private const string Bought = "COTS (bought; keeps its name)";
    private const string AfterModel = "Named after its model";
    private const string UseVaults = "Use the vault's file";
    private const string UpdateVaults = "Update the vault's file with this one";
    private const string Separate = "Import as a separate file";

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
                   "The original files aren't changed." + (scheme == null ? ""
                       : scheme.NameIsNumber ? $" Our parts and assemblies are named by the next part number ({scheme.Example()}) at the top of the vault, so describe each one; bought parts keep their names and go in {(scheme.CotsFolder.Length > 0 ? scheme.CotsFolder : "the vault")}. A drawing takes the name of the model it's named after."
                       : " Pick each file's group to give it the next part number.") +
                   (_inVault.Any(m => m != null) ? " For a file the vault already has, choose: use the vault's file (nothing is copied), update the vault's file with this one " +
                                                   "(it's checked out to you and this becomes its new version), or import it as a separate file under another name." : ""),
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "File", ReadOnly = true, FillWeight = 120 });
        var anyInVault = _inVault.Any(m => m != null);
        if (anyInVault)
        {
            var inVault = new DataGridViewComboBoxColumn { Name = "InVault", HeaderText = "Already in the vault", FillWeight = 100, FlatStyle = FlatStyle.Flat };
            inVault.Items.AddRange(UseVaults, UpdateVaults, Separate);
            _grid.Columns.Add(inVault);
        }
        if (scheme != null)
        {
            var group = new DataGridViewComboBoxColumn { Name = "Group", HeaderText = scheme.NameIsNumber ? "Ours or COTS" : "Group", FillWeight = 80, FlatStyle = FlatStyle.Flat };
            if (scheme.NameIsNumber)
            {
                group.Items.Add(Ours);
                group.Items.Add(Bought);
            }
            else
            {
                group.Items.Add(NoNumber);
                foreach (var g in scheme.Groups.OrderBy(g => g.Number)) group.Items.Add(g.ToString());
            }
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
                    row.Cells["InVault"] = new DataGridViewTextBoxCell { Value = "" };
                    row.Cells["InVault"].ReadOnly = true;
                }
                else
                {
                    // An identical file has nothing to update the vault's with.
                    var choice = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat };
                    choice.Items.Add(UseVaults);
                    if (!match.Value.Identical) choice.Items.Add(UpdateVaults);
                    choice.Items.Add(Separate);
                    row.Cells["InVault"] = choice;
                    choice.Value = UseVaults;
                    choice.ToolTipText = match.Value.Identical
                        ? $"Identical to the vault's {match.Value.RelativePath}. Use the vault's file: the imported files use that one. Import as a separate file: it's copied in under another name."
                        : $"The vault's {match.Value.RelativePath} has the same name but isn't the same file. Use the vault's file: this copy's changes aren't imported. " +
                          "Update the vault's file: this copy becomes its new version (the old one stays in History). Import as a separate file: for a different part that happens to share the name.";
                }
            }
            row.Cells["Name"].Value = scheme == null || scheme.NameIsNumber ? Path.GetFileNameWithoutExtension(path) : VaultImport.DescriptiveName(path, scheme);
            if (scheme?.NameIsNumber == true && IsDrawing(path))
            {
                row.Cells["Group"] = new DataGridViewTextBoxCell { Value = AfterModel };
                row.Cells["Group"].ReadOnly = true;
            }
            else if (scheme?.NameIsNumber == true)
            {
                row.Cells["Group"].Value = scheme.LooksBought(Path.GetFileName(path)) ? Bought : Ours;
            }
            else if (scheme != null)
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
        // With file names that are numbers, where a file goes follows from ours or COTS.
        if (scheme?.NameIsNumber != true) layout.Controls.Add(folderRow);
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

    private string TargetFolder => _scheme?.NameIsNumber == true || string.Equals(_folder.Text.Trim(), TopOfVault, StringComparison.Ordinal)
        ? "" : _folder.Text.Trim().Replace('\\', '/').Trim('/');

    private static bool IsDrawing(string path) => path.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase);

    /// <summary>Numbers every row in order, works out the new names and checks them.</summary>
    private void Recalculate()
    {
        var used = new List<string>(_used);
        var results = new (ImportItem, string?, string)[_files.Count];
        var problems = new List<string>();
        var nameIsNumber = _scheme?.NameIsNumber == true;
        // New names of the models, by their old name without extension, for drawings named after them.
        var modelNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Models first, so a drawing can take its model's new name.
        foreach (var i in Enumerable.Range(0, _files.Count).OrderBy(i => nameIsNumber && IsDrawing(_files[i].Path) ? 1 : 0))
        {
            var row = _grid.Rows[i];
            var path = _files[i].Path;
            var name = (row.Cells["Name"].Value as string ?? "").Trim();
            var description = (row.Cells["Description"].Value as string ?? "").Trim();
            var inVaultChoice = _inVault[i] == null ? null : row.Cells["InVault"].Value as string;
            var useVault = inVaultChoice == UseVaults;
            var updateVault = inVaultChoice == UpdateVaults;
            foreach (var column in new[] { "Group", "Name", "Description" }.Where(_grid.Columns.Contains)) row.Cells[column].ReadOnly = useVault || updateVault;
            if (useVault || updateVault)
            {
                var vaultPath = _inVault[i]!.Value.RelativePath;
                row.Cells["NewName"].Value = (useVault ? "Uses the vault's " : "Updates the vault's ") + vaultPath;
                results[i] = (new ImportItem(path, vaultPath, useExisting: useVault, replaceExisting: updateVault), null, "");
                modelNames[Path.GetFileNameWithoutExtension(path)] = vaultPath.Substring(0, vaultPath.Length - Path.GetExtension(vaultPath).Length);
                continue;
            }
            if (nameIsNumber)
            {
                results[i] = NumberedTarget(i, row, path, name, description, used, modelNames, problems);
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
            results[i] = (new ImportItem(path, target), partNumber, description);
        }
        var result = results.ToList();
        if (TargetFolder.Length > 0 && VaultService.FolderNameProblem(TargetFolder.Split('/').Last()) is { } folderProblem) problems.Add("Vault folder: " + folderProblem);
        problems.AddRange(VaultImport.Problems(result.Select(r => r.Item1).ToList(), _existsInVault));
        _problems.Text = string.Join(Environment.NewLine, problems.Distinct().Take(4));
        _ok.Enabled = problems.Count == 0 && result.Count > 0;
        Result = result;
    }

    /// <summary>
    /// With file names that are numbers: ours get the next number at the top of the vault (and need a description),
    /// bought ones keep their (editable) name in the COTS folder, and a drawing takes the new name of the model it's named after.
    /// </summary>
    private (ImportItem, string?, string) NumberedTarget(int i, DataGridViewRow row, string path, string name, string description, List<string> used,
        Dictionary<string, string> modelNames, List<string> problems)
    {
        var scheme = _scheme!;
        var extension = Path.GetExtension(path);
        var choice = row.Cells["Group"].Value as string;
        row.Cells["Name"].ReadOnly = choice != Bought;
        if (choice == AfterModel)
        {
            row.Cells["Group"].ReadOnly = true;
            // A drawing named like one of the imported models follows it; otherwise it keeps its name.
            var target = (modelNames.TryGetValue(Path.GetFileNameWithoutExtension(path), out var model) ? model : name) + extension;
            row.Cells["NewName"].Value = target;
            return (new ImportItem(path, target), null, description);
        }
        if (choice == Bought)
        {
            var fileName = (name.Length > 0 ? name : Path.GetFileNameWithoutExtension(path)) + extension;
            var target = scheme.CotsFolder.Length > 0 ? scheme.CotsFolder + "/" + fileName : fileName;
            row.Cells["NewName"].Value = target;
            modelNames[Path.GetFileNameWithoutExtension(path)] = target.Substring(0, target.Length - extension.Length);
            return (new ImportItem(path, target), null, description);
        }
        try
        {
            var own = scheme.NumberIn(path);
            var number = own != null && !used.Any(u => scheme.NumberIn(u) == own) ? own.Value : scheme.NextNumber(scheme.Groups[0], used);
            var partNumber = scheme.Format(number);
            used.Add(partNumber);
            var target = scheme.FileName(number, "") + extension;
            row.Cells["NewName"].Value = target;
            modelNames[Path.GetFileNameWithoutExtension(path)] = scheme.FileName(number, "");
            if (description.Length == 0) problems.Add($"Describe {Path.GetFileName(path)}: its file name will only be {partNumber}.");
            return (new ImportItem(path, target), partNumber, description);
        }
        catch (Core.Errors.PdmException ex)
        {
            problems.Add(ex.Message);
            return (new ImportItem(path, Path.GetFileName(path)), null, description);
        }
    }
}
