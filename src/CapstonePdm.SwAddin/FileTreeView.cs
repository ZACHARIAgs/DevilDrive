using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CapstonePdm.Core.Vault;

namespace CapstonePdm.SwAddin;

/// <summary>
/// The "files in use" tree: the active document and everything it uses, like the FeatureManager tree:
/// one row per component instance. Each row shows a status dot, the instance name, the version as local/vault (orange when
/// behind), and the part number and description. Owner-drawn so it matches the pane.
/// </summary>
internal sealed class FileTreeView : TreeView
{
    private const int MaxVisibleRows = 14;
    private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private string? _rootKey;

    /// <summary>Looks up a row's vault status (null for files outside the vault or not tracked).</summary>
    public Func<FileTreeNode, VaultFile?> StatusOf { get; set; } = _ => null;

    public FileTreeView()
    {
        DrawMode = TreeViewDrawMode.OwnerDrawAll;
        ShowLines = false;
        ShowPlusMinus = false;
        ShowRootLines = false;
        FullRowSelect = true;
        HideSelection = false;
        HotTracking = false;
        ShowNodeToolTips = true;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Card;
        Font = Theme.Body;
        ItemHeight = Theme.Px(38);
        Indent = Theme.Px(16);
        Margin = new Padding(0, 0, 0, Theme.Px(8));
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    public FileTreeNode? Root { get; private set; }

    public FileTreeNode? SelectedFile => SelectedNode?.Tag as FileTreeNode;

    /// <summary>Replaces the tree, keeping which subassemblies were expanded (the top level is open by default).</summary>
    public void Show(FileTreeNode? root)
    {
        BeginUpdate();
        try
        {
            var selectedKey = SelectedNode?.Name;
            Nodes.Clear();
            Root = root;
            if (root != null)
            {
                if (!string.Equals(_rootKey, root.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    _rootKey = root.FullPath;
                    _expanded.Clear();
                    _expanded.Add(Key(null, root));
                }
                var top = Add(Nodes, null, root);
                if (selectedKey != null) SelectedNode = Nodes.Find(selectedKey, searchAllChildren: true).FirstOrDefault();
                SelectedNode ??= top;
            }
        }
        finally
        {
            EndUpdate();
        }
        RefreshStatus();
        FitHeight();
    }

    private TreeNode Add(TreeNodeCollection into, TreeNode? parent, FileTreeNode file)
    {
        var key = Key(parent, file);
        var node = new TreeNode(file.DisplayName) { Name = key, Tag = file };
        into.Add(node);
        foreach (var child in file.Children) Add(node.Nodes, node, child);
        if (_expanded.Contains(key)) node.Expand();
        return node;
    }

    /// <summary>Re-reads statuses (after a vault refresh) without rebuilding the tree.</summary>
    public void RefreshStatus()
    {
        foreach (var node in AllNodes(Nodes)) node.ToolTipText = ToolTip(node.Tag as FileTreeNode);
        Invalidate();
    }

    private static string Key(TreeNode? parent, FileTreeNode file) =>
        (parent?.Name ?? "") + "|" + file.DisplayName + "|" + file.FullPath;

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in AllNodes(node.Nodes)) yield return child;
        }
    }

    private void FitHeight()
    {
        var visible = 0;
        for (var n = Nodes.Count > 0 ? Nodes[0] : null; n != null && visible <= MaxVisibleRows; n = n.NextVisibleNode) visible++;
        Height = Math.Max(1, Math.Min(visible, MaxVisibleRows)) * ItemHeight + Theme.Px(2);
    }

    protected override void OnAfterExpand(TreeViewEventArgs e)
    {
        if (e.Node != null) _expanded.Add(e.Node.Name);
        FitHeight();
        base.OnAfterExpand(e);
    }

    protected override void OnAfterCollapse(TreeViewEventArgs e)
    {
        if (e.Node != null) _expanded.Remove(e.Node.Name);
        FitHeight();
        base.OnAfterCollapse(e);
    }

    protected override void OnNodeMouseClick(TreeNodeMouseClickEventArgs e)
    {
        // Our own chevron (the built-in plus/minus is off because the rows are custom-drawn).
        if (e.Node.Nodes.Count > 0 && e.X < ChevronRight(e.Node)) e.Node.Toggle();
        base.OnNodeMouseClick(e);
    }

    private int ChevronRight(TreeNode node) => Theme.Px(6) + node.Level * Indent + Theme.Px(16);

    protected override void OnDrawNode(DrawTreeNodeEventArgs e)
    {
        if (e.Node?.Tag is not FileTreeNode file || e.Bounds.Height <= 0) return;
        var g = e.Graphics;
        var row = new Rectangle(0, e.Bounds.Top, ClientSize.Width, e.Bounds.Height);
        var selected = (e.State & TreeNodeStates.Selected) != 0;
        using (var back = new SolidBrush(selected ? Color.FromArgb(0xE8, 0xF0, 0xFE) : Theme.Card)) g.FillRectangle(back, row);

        var status = StatusOf(file);
        var x = Theme.Px(6) + e.Node.Level * Indent;
        if (e.Node.Nodes.Count > 0)
        {
            TextRenderer.DrawText(g, e.Node.IsExpanded ? "" : "", Theme.SmallGlyph, new Rectangle(x, row.Top, Theme.Px(16), Theme.Px(22)),
                Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        x += Theme.Px(18);

        // Status dot.
        var dot = Theme.Px(9);
        var dotRect = new Rectangle(x, row.Top + Theme.Px(7), dot, dot);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(DotColor(file, status))) g.FillEllipse(brush, dotRect);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        x += dot + Theme.Px(6);

        // Version on the right: local/vault, orange when this PC is behind.
        var version = status?.VersionText ?? "";
        var behind = status != null && status.Version < status.VaultVersion;
        var versionWidth = version.Length == 0 ? 0 : TextRenderer.MeasureText(version, Theme.Small).Width;
        var right = row.Right - Theme.Px(6);
        if (versionWidth > 0)
        {
            TextRenderer.DrawText(g, version, behind ? Theme.SmallBold : Theme.Small, new Rectangle(right - versionWidth, row.Top + Theme.Px(3), versionWidth, Theme.Px(16)),
                behind ? Color.FromArgb(0xC2, 0x41, 0x0C) : Theme.Muted, TextFormatFlags.Right);
            right -= versionWidth + Theme.Px(6);
        }

        // Line 1: instance name (as in the FeatureManager tree) and configuration.
        var name = file.DisplayName + (file.Configuration.Length > 0 && e.Node.Level > 0 ? $" ({file.Configuration})" : "");
        var nameColor = file.IsSuppressed ? Theme.DisabledText : Theme.Text;
        TextRenderer.DrawText(g, name, e.Node.Level == 0 ? Theme.Strong : Theme.Body, new Rectangle(x, row.Top + Theme.Px(2), Math.Max(1, right - x), Theme.Px(18)),
            nameColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Line 2: part number and description, or why the row needs attention.
        string detail;
        var detailColor = Theme.Muted;
        if (file.IsOutsideVault)
        {
            detail = "⚠ Outside the vault: teammates won't have it";
            detailColor = Color.FromArgb(0xB4, 0x53, 0x09);
        }
        else if (file.IsSuppressed)
        {
            detail = "Suppressed";
        }
        else
        {
            var partNumber = status?.Index?.GetProperty(IndexEntry.PartNumberNames) ?? System.IO.Path.GetFileNameWithoutExtension(file.Name);
            var description = status?.Index?.GetProperty(IndexEntry.DescriptionNames) ?? "";
            detail = description.Length > 0 ? $"{partNumber} — {description}" : partNumber;
        }
        TextRenderer.DrawText(g, detail, Theme.Small, new Rectangle(x, row.Top + Theme.Px(19), Math.Max(1, row.Right - Theme.Px(6) - x), Theme.Px(16)),
            detailColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static Color DotColor(FileTreeNode file, VaultFile? status)
    {
        if (file.IsOutsideVault) return Color.FromArgb(0xF5, 0x9E, 0x0B);
        if (file.IsSuppressed || status == null) return Color.FromArgb(0xC4, 0xC9, 0xD2);
        return status.State switch
        {
            FileState.CheckedOutByMe => Color.FromArgb(0x25, 0x63, 0xEB),
            FileState.CheckedOutByOther => Color.FromArgb(0xDC, 0x26, 0x26),
            FileState.Available => Color.FromArgb(0x16, 0xA3, 0x4A),
            FileState.Frozen => Color.FromArgb(0x47, 0x55, 0x69),
            FileState.InReview => Color.FromArgb(0x0D, 0x94, 0x88),
            FileState.ModifiedWithoutCheckOut or FileState.Modified => Color.FromArgb(0xEA, 0x58, 0x0C),
            FileState.NotInVault => Color.FromArgb(0x7C, 0x3A, 0xED),
            _ => Color.FromArgb(0x9C, 0xA3, 0xAF),
        };
    }

    private string ToolTip(FileTreeNode? file)
    {
        if (file == null) return "";
        if (file.IsOutsideVault) return $"{file.FullPath}\nOutside the vault: teammates won't have this file. Save a copy inside the vault and replace it.";
        var status = StatusOf(file);
        var state = status?.State switch
        {
            FileState.CheckedOutByMe => "Checked out by you",
            FileState.CheckedOutByOther => "Checked out by " + status!.CheckedOutBy,
            FileState.Available => "Available (not checked out)",
            FileState.Frozen => "Frozen by " + status!.FrozenBy,
            FileState.InReview => "In review (submitted by " + status!.ReviewSubmittedBy + ")",
            FileState.ModifiedWithoutCheckOut => "Edited without check-out",
            FileState.Modified => "Edited, not checked in",
            FileState.NotInVault => "New, not in the vault yet",
            null => "Not in the vault yet",
            _ => status!.State.ToString(),
        };
        var lines = new List<string> { file.RelativePath ?? file.FullPath, state };
        if (status != null && status.VaultVersion > 0)
        {
            lines.Add(status.Version < status.VaultVersion
                ? $"Version {status.Version} on this PC, {status.VaultVersion} in the vault: click Get Latest"
                : $"Version {status.Version} (up to date)");
        }
        if (file.IsSuppressed) lines.Add("Suppressed in this assembly");
        return string.Join("\n", lines);
    }
}
