using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CapstonePdm.SwAddin;

/// <summary>Asks for a new name and/or folder (inside the vault) for a file.</summary>
internal sealed class RenameForm : Form
{
    private readonly string _vaultRoot;
    private readonly string _extension;
    private readonly string _oldPath;
    private readonly TextBox _name = new TextBox { Dock = DockStyle.Fill };
    private readonly TextBox _folder = new TextBox { Dock = DockStyle.Fill };
    private readonly Label _error = new Label { AutoSize = true, ForeColor = Color.FromArgb(0xB9, 0x1C, 0x1C) };
    private readonly Button _ok = new Button { Text = "Rename", DialogResult = DialogResult.OK, AutoSize = true };

    public RenameForm(string oldPath, string vaultRoot)
    {
        _oldPath = oldPath;
        _vaultRoot = vaultRoot.TrimEnd('\\');
        _extension = Path.GetExtension(oldPath);
        Text = "Rename / Move";
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = $"Renaming or moving {Path.GetFileName(oldPath)} also updates every assembly and drawing that uses it, " +
                   "and checks them all in together so nobody's references break.",
            AutoSize = true,
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 3);

        layout.Controls.Add(new Label { Text = "New name:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_name, 1, 1);
        layout.Controls.Add(new Label { Text = _extension, AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);

        layout.Controls.Add(new Label { Text = "Folder:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_folder, 1, 2);
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => Browse();
        layout.Controls.Add(browse, 2, 2);

        layout.Controls.Add(_error, 0, 3);
        layout.SetColumnSpan(_error, 3);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 3);

        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = cancel;

        _name.Text = Path.GetFileNameWithoutExtension(oldPath);
        _folder.Text = Path.GetDirectoryName(oldPath) ?? _vaultRoot;
        _name.TextChanged += (_, _) => Validate();
        _folder.TextChanged += (_, _) => Validate();
        Validate();

        Load += (_, _) =>
        {
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Size = new Size((int)(560 * scale), (int)(250 * scale));
            MinimumSize = Size;
            intro.MaximumSize = new Size(ClientSize.Width - (int)(30 * scale), 0);
            _name.Focus();
            _name.SelectAll();
        };
    }

    public string NewPath => Path.Combine(_folder.Text.Trim(), _name.Text.Trim() + _extension);

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : _vaultRoot, Description = "Choose a folder inside the vault" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
    }

    private new void Validate()
    {
        var name = _name.Text.Trim();
        var folder = _folder.Text.Trim().TrimEnd('\\');
        string? problem = null;
        if (name.Length == 0) problem = "Enter a name.";
        else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) problem = "The name contains characters Windows doesn't allow.";
        else if (!(folder.Equals(_vaultRoot, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(_vaultRoot + "\\", StringComparison.OrdinalIgnoreCase)))
            problem = $"The folder must be inside the vault ({_vaultRoot}).";
        else if (folder.Split('\\').Any(p => p.StartsWith(".", StringComparison.Ordinal))) problem = "Choose a normal vault folder.";
        else if (string.Equals(NewPath, _oldPath, StringComparison.OrdinalIgnoreCase)) problem = "That's the current name and folder.";
        else if (File.Exists(NewPath)) problem = "A file with that name already exists there.";

        _error.Text = problem ?? "";
        _ok.Enabled = problem == null;
    }
}
