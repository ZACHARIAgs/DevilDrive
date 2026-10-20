using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CapstonePdm.Core.Vault;

/// <summary>
/// One component instance as SolidWorks reports it. The add-in fills these from the component tree;
/// drawings list the models they show as children.
/// </summary>
public sealed class RawComponent
{
    public string Path { get; set; } = "";
    /// <summary>SolidWorks' instance name, e.g. "bracket-2". Empty for a drawing's models.</summary>
    public string InstanceName { get; set; } = "";
    public string Configuration { get; set; } = "";
    public bool IsSuppressed { get; set; }
    public List<RawComponent> Children { get; set; } = new List<RawComponent>();
}

/// <summary>A row in the "files in use" tree: one component instance (or the top document, or a drawing's model).</summary>
public sealed class FileTreeNode
{
    public string FullPath { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(FullPath);
    /// <summary>What the row shows: "bracket&lt;2&gt;" for an instance (as in the FeatureManager tree), else the file name.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Path relative to the vault root with forward slashes; null when the file is outside the vault.</summary>
    public string? RelativePath { get; set; }
    public bool IsOutsideVault => RelativePath == null;
    public string Configuration { get; set; } = "";
    public bool IsSuppressed { get; set; }
    public List<FileTreeNode> Children { get; } = new List<FileTreeNode>();

    /// <summary>This node and everything below it, depth first.</summary>
    public IEnumerable<FileTreeNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.SelfAndDescendants()) yield return node;
        }
    }

    /// <summary>Distinct files used anywhere in the tree (several instances of a part count once).</summary>
    public IEnumerable<string> DistinctFiles() =>
        SelfAndDescendants().Select(n => n.FullPath).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rows for files outside the vault anywhere below (teammates won't have them).</summary>
    public IEnumerable<FileTreeNode> OutsideVault() => SelfAndDescendants().Where(n => n.IsOutsideVault);
}

public static class FileTree
{
    private static readonly Regex InstanceSuffix = new Regex(@"-(\d+)$");

    /// <summary>
    /// One row per component instance, like the FeatureManager tree. Suppressed instances are shown
    /// without their contents, and files outside the vault are marked. Subassemblies come first, then
    /// parts by name and instance number.
    /// </summary>
    public static FileTreeNode Build(string rootPath, IEnumerable<RawComponent> children, string vaultRoot)
    {
        var root = new FileTreeNode { FullPath = rootPath, DisplayName = System.IO.Path.GetFileName(rootPath), RelativePath = Relative(rootPath, vaultRoot) };
        AddChildren(root, children, vaultRoot);
        return root;
    }

    private static void AddChildren(FileTreeNode parent, IEnumerable<RawComponent> raw, string vaultRoot)
    {
        var ordered = raw
            .Where(c => !string.IsNullOrWhiteSpace(c.Path))
            .OrderBy(c => c.Path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => BaseName(c), StringComparer.OrdinalIgnoreCase)
            .ThenBy(InstanceNumber);

        foreach (var component in ordered)
        {
            var node = new FileTreeNode
            {
                FullPath = component.Path,
                DisplayName = DisplayNameOf(component),
                RelativePath = Relative(component.Path, vaultRoot),
                Configuration = component.Configuration ?? "",
                IsSuppressed = component.IsSuppressed,
            };
            if (!node.IsSuppressed) AddChildren(node, component.Children, vaultRoot);
            parent.Children.Add(node);
        }
    }

    /// <summary>"bracket-2" becomes "bracket&lt;2&gt;", as SolidWorks shows it; a drawing's models show their file name.</summary>
    public static string DisplayNameOf(RawComponent component)
    {
        var name = component.InstanceName ?? "";
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name.Substring(slash + 1); // Name2 can include parent instances: "sub-1/bracket-2"
        if (name.Length == 0) return System.IO.Path.GetFileName(component.Path);
        var match = InstanceSuffix.Match(name);
        return match.Success ? name.Substring(0, match.Index) + "<" + match.Groups[1].Value + ">" : name;
    }

    private static string BaseName(RawComponent c)
    {
        var display = DisplayNameOf(c);
        var bracket = display.LastIndexOf('<');
        return bracket > 0 ? display.Substring(0, bracket) : display;
    }

    private static int InstanceNumber(RawComponent c)
    {
        var match = InstanceSuffix.Match(c.InstanceName ?? "");
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
    }

    /// <summary>Vault-relative path with forward slashes, or null if the file isn't inside the vault.</summary>
    public static string? Relative(string fullPath, string vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        string full, root;
        try
        {
            full = System.IO.Path.GetFullPath(fullPath);
            root = System.IO.Path.GetFullPath(vaultRoot).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length).Replace('\\', '/') : null;
    }
}
