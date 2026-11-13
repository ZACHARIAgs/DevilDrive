using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Jarvis.Core.Help;
using Jarvis.Core.Vault;

namespace Jarvis.SwAddin;

/// <summary>Help from the SolidWorks pane: the same common problems and fixes as the app's Help window (HelpTopics).</summary>
internal sealed class HelpForm : Form
{
    private readonly TextBox _search = new TextBox { Dock = DockStyle.Top };
    private readonly TextBox _text = new TextBox
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window, BorderStyle = BorderStyle.None,
    };

    public HelpForm()
    {
        Text = "DevilDrive Help";
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.None;

        var intro = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6),
            Text = "Common problems and how to fix them. Type words from the message you got to find it.",
        };
        _search.TextChanged += (_, _) => ShowTopics(_search.Text);
        var backups = new Button { Text = "Open backups folder", AutoSize = true, MinimumSize = new Size(140, 28) };
        backups.Click += (_, _) =>
        {
            Directory.CreateDirectory(FileUtil.BackupRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FileUtil.BackupRoot}\"") { UseShellExecute = true });
        };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(backups);
        var textPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        textPanel.Controls.Add(_text);

        var layout = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        layout.Controls.Add(textPanel);
        layout.Controls.Add(buttons);
        layout.Controls.Add(_search);
        layout.Controls.Add(intro);
        Controls.Add(layout);
        CancelButton = close;
        Load += (_, _) =>
        {
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Size = new Size((int)(640 * scale), (int)(620 * scale));
            if (Owner == null) CenterToScreen();
            _search.Focus();
        };
        ShowTopics("");
    }

    private void ShowTopics(string query)
    {
        var topics = HelpTopics.All.Where(t => t.Matches(query)).ToList();
        _text.Text = topics.Count == 0
            ? "Nothing matches. Try fewer words."
            : string.Join(Environment.NewLine + Environment.NewLine, topics.Select(t => t.ToString().Replace("\n", Environment.NewLine)));
        _text.SelectionStart = 0;
    }
}
