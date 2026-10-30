using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jarvis.Core.Vault;
using Xunit;

namespace Jarvis.Core.Tests;

/// <summary>Exporting files from the vault (like Pack and Go): what goes, and where.</summary>
public sealed class VaultExportTests
{
    private static readonly Dictionary<string, string[]> Uses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Turret/Turret.SLDASM"] = new[] { "Turret/Base.SLDPRT", "Turret/Bearing.SLDASM" },
        ["Turret/Bearing.SLDASM"] = new[] { "COTS/Ball.SLDPRT", "Turret/Base.SLDPRT" },
        ["Turret/Base.SLDPRT"] = new[] { @"C:\Users\bob\Desktop\stock.SLDPRT" },
        ["Drawings/Base.SLDDRW"] = new[] { "Turret/Base.SLDPRT", "Turret/Washer.SLDPRT" },
        ["Drawings/Turret.SLDDRW"] = new[] { "Turret/Turret.SLDASM" },
    };

    private static IReadOnlyList<string> References(string path) => Uses.TryGetValue(path, out var refs) ? refs : Array.Empty<string>();

    private static IEnumerable<string> DrawingsOf(string path) =>
        Uses.Where(u => u.Key.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase) && u.Value.Contains(path, StringComparer.OrdinalIgnoreCase)).Select(u => u.Key);

    [Fact]
    public void Export_TakesEverythingTheFileUses_AllLevels_Once()
    {
        var set = VaultExport.Collect(new[] { "Turret/Turret.SLDASM" }, References, DrawingsOf, includeDrawings: false);

        Assert.Equal(new[] { "Turret/Turret.SLDASM", "Turret/Base.SLDPRT", "Turret/Bearing.SLDASM", "COTS/Ball.SLDPRT" }, set.Files);
        Assert.Empty(set.Drawings);
        Assert.Equal(new[] { @"Base.SLDPRT uses C:\Users\bob\Desktop\stock.SLDPRT" }, set.Outside);
    }

    [Fact]
    public void Export_WithDrawings_AddsThemAndWhatTheyShow()
    {
        var set = VaultExport.Collect(new[] { "Turret/Turret.SLDASM" }, References, DrawingsOf, includeDrawings: true);

        Assert.Equal(new[] { "Drawings/Base.SLDDRW", "Drawings/Turret.SLDDRW" }, set.Drawings);
        Assert.Contains("Turret/Washer.SLDPRT", set.Files); // only on the base's drawing
        Assert.Equal(set.Files.Count, set.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Export_Items_KeepFoldersOrFlatten()
    {
        var files = new[] { "Turret/Base.SLDPRT", "COTS/Ball.SLDPRT" };

        var kept = VaultExport.Items(@"C:\Vault", files, keepFolders: true);
        var flat = VaultExport.Items(@"C:\Vault", files, keepFolders: false);

        Assert.Equal(@"C:\Vault\Turret\Base.SLDPRT", kept[0].Source);
        Assert.Equal("Turret/Base.SLDPRT", kept[0].Target);
        Assert.Equal("Ball.SLDPRT", flat[1].Target);
        Assert.Equal("Base and 1 more", VaultExport.DefaultName(files));
    }

    [Fact]
    public void Export_CopiesAndPointsTheCopiesAtEachOther()
    {
        var root = Path.Combine(Path.GetTempPath(), "capstone-pdm-tests", Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "Vault");
        var export = Path.Combine(root, "Export");
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, "Turret"));
            File.WriteAllText(Path.Combine(vault, "Turret", "Base.SLDPRT"), "base");
            File.WriteAllText(Path.Combine(vault, "Turret", "Turret.SLDASM"), "assembly");
            File.SetAttributes(Path.Combine(vault, "Turret", "Base.SLDPRT"), FileAttributes.ReadOnly); // checked in = read-only
            var solidWorks = new FakeEditor();
            solidWorks.Uses[Path.Combine(export, "Turret.SLDASM")] = new List<string> { Path.Combine(vault, "Turret", "Base.SLDPRT") };

            var items = VaultExport.Items(vault, new[] { "Turret/Turret.SLDASM", "Turret/Base.SLDPRT" }, keepFolders: false);
            VaultImport.CopyAndRepoint(export, items, solidWorks);

            Assert.Equal(new[] { Path.Combine(export, "Base.SLDPRT") }, solidWorks.Uses[Path.Combine(export, "Turret.SLDASM")]);
            Assert.False(new FileInfo(Path.Combine(export, "Base.SLDPRT")).IsReadOnly);
            Assert.True(new FileInfo(Path.Combine(vault, "Turret", "Base.SLDPRT")).IsReadOnly); // the vault's file is untouched
        }
        finally
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeEditor : IReferenceEditor
    {
        public Dictionary<string, List<string>> Uses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> GetReferences(string fullPath) => Uses.TryGetValue(fullPath, out var refs) ? refs : new List<string>();

        public bool ReplaceReference(string referencingFile, string oldReference, string newReference)
        {
            var list = Uses[referencingFile];
            list[list.FindIndex(r => string.Equals(r, oldReference, StringComparison.OrdinalIgnoreCase))] = newReference;
            return true;
        }

        public void Release(IReadOnlyList<string> fullPaths)
        {
        }

        public void Restore(IReadOnlyDictionary<string, string> movedFullPaths)
        {
        }
    }
}
