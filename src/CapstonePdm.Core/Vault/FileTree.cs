using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CapstonePdm.Core.Vault;

/// <summary>
/// One component as SolidWorks reports it (one instance in its parent), before grouping. The add-in
/// fills these from the component tree; drawings list the models they show as children.
/// </summary>
public sealed class RawComponent
{
    public string Path { get; set; } = "";
    public string Configuration { get; set; } = "";
    public bool IsSuppressed { get; set; }
    public List<RawComponent> Children { get; set; } = new List<RawComponent>();
}

/// <summary>A row in the "files in use" tree: one file (and configuration) under one parent.</summary>
public sealed class FileTreeNode
{
    public string FullPath { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(FullPath);
    /// <summary>Path relative to the vault root with forward slashes; null when the file is outside the vault.</summary>
    public string? RelativePath { get; set; }
    public bool IsOutsideVault => RelativePath == null;
    public string Configuration { get; set; } = "";
    /// <summary>How many times the parent uses it (instances with the same file, configuration and suppression).</summary>
    public int Quantity { get; set; } = 1;
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

    /// <summary>Files outside the vault anywhere below (teammates won't have them).</summary>
    public IEnumerable<FileTreeNode> OutsideVault() => SelfAndDescendants().Where(n => n.IsOutsideVault);
}

public static class FileTree
{
    /// <summary>
    /// Groups instances of the same file under each parent into one row with a quantity, keeps
    /// suppressed instances as their own rows, and marks files outside the vault. Rows are sorted
    /// with subassemblies first, then by name.
    /// </summary>
    public static FileTreeNode Build(string rootPath, IEnumerable<RawComponent> children, string vaultRoot)
    {
        var root = new FileTreeNode { FullPath = rootPath, RelativePath = Relative(rootPath, vaultRoot) };
        AddChildren(root, children, vaultRoot);
        return root;
    }

    private static void AddChildren(FileTreeNode parent, IEnumerable<RawComponent> raw, string vaultRoot)
    {
        var groups = raw
            .Where(c => !string.IsNullOrWhiteSpace(c.Path))
            .GroupBy(c => (Path: Normalize(c.Path), Config: c.Configuration ?? "", c.IsSuppressed))
            .OrderBy(g => IsAssembly(g.Key.Path) ? 0 : 1)
            .ThenBy(g => System.IO.Path.GetFileName(g.Key.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Config, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.IsSuppressed);

        foreach (var group in groups)
        {
            var first = group.First();
            var node = new FileTreeNode
            {
                FullPath = first.Path,
                RelativePath = Relative(first.Path, vaultRoot),
                Configuration = group.Key.Config,
                Quantity = group.Count(),
                IsSuppressed = group.Key.IsSuppressed,
            };
            // Every instance of a subassembly in one configuration has the same contents; show them once.
            if (!node.IsSuppressed) AddChildren(node, first.Children, vaultRoot);
            parent.Children.Add(node);
        }
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

    private static string Normalize(string path) => path.Replace('/', '\\').ToLowerInvariant();

    private static bool IsAssembly(string path) => path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
}
