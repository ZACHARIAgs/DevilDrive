using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;
using Xunit;

namespace Jarvis.Core.Tests;

/// <summary>Importing a new member's SolidWorks files: names, copying, and pointing assemblies at the copies.</summary>
public sealed class VaultImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "capstone-pdm-tests", Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _vault;

    public VaultImportTests()
    {
        _source = Path.Combine(_root, "Their Project");
        _vault = Path.Combine(_root, "Vault");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_vault);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Stands in for SolidWorks: which file uses which, by full path; rewrites are recorded.</summary>
    private sealed class FakeSolidWorks : IReferenceEditor
    {
        public Dictionary<string, List<string>> Uses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Fail { get; set; }

        /// <summary>Copy (full path) to the original it was copied from: a copy holds the original's references until replaced.</summary>
        public Dictionary<string, string> CopyOf { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> GetReferences(string fullPath)
        {
            if (Uses.TryGetValue(fullPath, out var own)) return own;
            return CopyOf.TryGetValue(fullPath, out var original) && Uses.TryGetValue(original, out var inherited) ? inherited : new List<string>();
        }

        public bool ReplaceReference(string referencingFile, string oldReference, string newReference)
        {
            if (Fail) return false;
            if (!Uses.ContainsKey(referencingFile)) Uses[referencingFile] = new List<string>(GetReferences(referencingFile));
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

    private string Source(string name, string content = "x")
    {
        var path = Path.Combine(_source, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Import_CopiesUnderNewNames_AndPointsTheAssemblyAtTheCopies()
    {
        var plate = Source("clamp plate.SLDPRT", "plate");
        var bolt = Source("bolt.SLDPRT", "bolt");
        var assembly = Source("clamp.SLDASM", "assembly");
        var solidWorks = new FakeSolidWorks();
        solidWorks.Uses[assembly] = new List<string> { plate, bolt };
        var items = new[]
        {
            new ImportItem(plate, "Turret/TLT-209_Clamp_Plate.SLDPRT"),
            new ImportItem(bolt, "Turret/TLT-210_Bolt.SLDPRT"),
            new ImportItem(assembly, "Turret/TLT-211_Clamp.SLDASM"),
        };
        foreach (var item in items) solidWorks.CopyOf[Path.Combine(_vault, item.Target.Replace('/', '\\'))] = item.Source;

        var copies = VaultImport.CopyAndRepoint(_vault, items, solidWorks);

        Assert.Equal(3, copies.Count);
        Assert.Equal("plate", File.ReadAllText(Path.Combine(_vault, "Turret", "TLT-209_Clamp_Plate.SLDPRT")));
        var copiedAssembly = Path.Combine(_vault, "Turret", "TLT-211_Clamp.SLDASM");
        Assert.Equal(new[] { Path.Combine(_vault, "Turret", "TLT-209_Clamp_Plate.SLDPRT"), Path.Combine(_vault, "Turret", "TLT-210_Bolt.SLDPRT") },
            solidWorks.Uses[copiedAssembly]);
        Assert.Equal(new[] { plate, bolt }, solidWorks.Uses[assembly]); // the original is untouched
        Assert.True(File.Exists(plate));
    }

    [Fact]
    public void Import_PackAndGoCopy_StoredPathsFromAnotherPc_AreMatchedByName()
    {
        var pin = Source("pin.SLDPRT", "pin");
        var assembly = Source("Tilt_Prevention.SLDASM", "assembly");
        var solidWorks = new FakeSolidWorks();
        // Saved on a teammate's PC: the assembly still names their folder, and a part that isn't imported.
        solidWorks.Uses[assembly] = new List<string> { @"C:\Users\bob\Desktop\tilt_prevention\pin.SLDPRT", @"C:\Users\bob\Desktop\other\washer.SLDPRT" };
        var items = new[] { new ImportItem(pin, "pin.SLDPRT"), new ImportItem(assembly, "Tilt_Prevention.SLDASM") };
        foreach (var item in items) solidWorks.CopyOf[Path.Combine(_vault, item.Target)] = item.Source;

        VaultImport.CopyAndRepoint(_vault, items, solidWorks);

        Assert.Equal(new[] { Path.Combine(_vault, "pin.SLDPRT"), @"C:\Users\bob\Desktop\other\washer.SLDPRT" },
            solidWorks.Uses[Path.Combine(_vault, "Tilt_Prevention.SLDASM")]);
    }

    [Fact]
    public void Import_UsesTheVaultsFile_InsteadOfCopyingItAgain()
    {
        var coupon = Source("Type V Coupon.SLDPRT", "coupon");
        var assembly = Source("Torsion Fixture.SLDASM", "assembly");
        File.WriteAllText(Path.Combine(_vault, "Type V Coupon.SLDPRT"), "coupon");
        var solidWorks = new FakeSolidWorks();
        solidWorks.Uses[assembly] = new List<string> { @"C:\Users\bob\Desktop\pack\Type V Coupon.SLDPRT" };
        var items = new[] { new ImportItem(coupon, "Type V Coupon.SLDPRT", useExisting: true), new ImportItem(assembly, "Fixtures/Torsion Fixture.SLDASM") };
        solidWorks.CopyOf[Path.Combine(_vault, "Fixtures", "Torsion Fixture.SLDASM")] = assembly;

        Assert.Empty(VaultImport.Problems(items, path => path == "Type V Coupon.SLDPRT"));
        var copies = VaultImport.CopyAndRepoint(_vault, items, solidWorks);

        Assert.Equal(new[] { Path.Combine(_vault, "Fixtures", "Torsion Fixture.SLDASM") }, copies);
        Assert.Equal(new[] { Path.Combine(_vault, "Type V Coupon.SLDPRT") }, solidWorks.Uses[Path.Combine(_vault, "Fixtures", "Torsion Fixture.SLDASM")]);
        Assert.Contains(VaultImport.Problems(items, _ => false), p => p.Contains("isn't in the vault"));
    }

    [Fact]
    public void Import_PutsEverythingBack_WhenSolidWorksCantRepoint()
    {
        var plate = Source("plate.SLDPRT");
        var assembly = Source("clamp.SLDASM");
        var solidWorks = new FakeSolidWorks { Fail = true };
        solidWorks.Uses[assembly] = new List<string> { plate };
        var items = new[] { new ImportItem(plate, "TLT-209_Plate.SLDPRT"), new ImportItem(assembly, "TLT-210_Clamp.SLDASM") };
        foreach (var item in items) solidWorks.CopyOf[Path.Combine(_vault, item.Target)] = item.Source;

        var ex = Assert.Throws<PdmException>(() => VaultImport.CopyAndRepoint(_vault, items, solidWorks));

        Assert.Contains("Nothing was imported", ex.Message);
        Assert.Empty(Directory.GetFiles(_vault, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Import_Problems()
    {
        var a = Source("a.SLDPRT");
        var b = Source("b.SLDPRT");
        var items = new[] { new ImportItem(a, "Turret/TLT-209.SLDPRT"), new ImportItem(b, "turret/tlt-209.sldprt"), new ImportItem(b, "Turret/Bad:Name.SLDPRT") };

        var problems = VaultImport.Problems(items, path => path == "Turret/Bad:Name.SLDPRT");

        Assert.Contains(problems, p => p.Contains("would all become"));
        Assert.Contains(problems, p => p.Contains("already a Turret/Bad:Name.SLDPRT"));
        Assert.Contains(problems, p => p.Contains("isn't a valid file name"));
        Assert.Empty(VaultImport.Problems(new[] { new ImportItem(a, "Turret/TLT-209.SLDPRT") }, _ => false));
    }

    [Theory]
    [InlineData("TLT-203_Bearing_Housing.SLDPRT", "Bearing Housing")]
    [InlineData("TLT-203-Bearing_Housing.SLDPRT", "Bearing Housing")]
    [InlineData("clamp plate v2.SLDPRT", "clamp plate v2")]
    [InlineData("TLT-203.SLDPRT", "")]
    public void Import_DescriptiveName(string file, string expected)
    {
        var scheme = new NumberingScheme { Prefix = "TLT", Digits = 3, Groups = new List<NumberGroup> { new NumberGroup { Number = 200, Name = "Turret" } } };
        Assert.Equal(expected, VaultImport.DescriptiveName(file, scheme));
    }
}
