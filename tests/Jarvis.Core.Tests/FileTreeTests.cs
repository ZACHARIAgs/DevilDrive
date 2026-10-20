using System.Linq;
using Jarvis.Core.Vault;
using Xunit;

namespace Jarvis.Core.Tests;

public class FileTreeTests
{
    private const string Vault = @"C:\CapstoneVault";

    private static RawComponent C(string path, string instance, bool suppressed = false, params RawComponent[] children) =>
        new RawComponent { Path = path, InstanceName = instance, IsSuppressed = suppressed, Configuration = "Default", Children = children.ToList() };

    [Fact]
    public void ShowsEveryInstance_LikeTheFeatureManagerTree()
    {
        var part = @"C:\CapstoneVault\PDM initial test part.SLDPRT";
        var tree = FileTree.Build(@"C:\CapstoneVault\TEST ASSEMBLY.SLDASM",
            new[] { C(part, "PDM initial test part-2"), C(part, "PDM initial test part-1") }, Vault);

        Assert.Equal(new[] { "PDM initial test part<1>", "PDM initial test part<2>" }, tree.Children.Select(n => n.DisplayName));
        Assert.Equal(2, tree.DistinctFiles().Count());
    }

    [Fact]
    public void SubassembliesFirst_WithTheirOwnInstances_SuppressedWithoutContents()
    {
        var wheel = @"C:\CapstoneVault\cart\wheel.sldasm";
        var spoke = @"C:\CapstoneVault\cart\spoke.sldprt";
        var tree = FileTree.Build(@"C:\CapstoneVault\cart\cart.sldasm", new[]
        {
            C(@"C:\CapstoneVault\cart\bolt.sldprt", "bolt-10"),
            C(@"C:\CapstoneVault\cart\bolt.sldprt", "bolt-9", suppressed: true),
            C(wheel, "wheel-1", false, C(spoke, "wheel-1/spoke-1"), C(spoke, "wheel-1/spoke-2")),
            C(wheel, "wheel-2", true, C(spoke, "wheel-2/spoke-1")),
        }, Vault);

        Assert.Equal(new[] { "wheel<1>", "wheel<2>", "bolt<9>", "bolt<10>" }, tree.Children.Select(n => n.DisplayName));
        Assert.Equal(new[] { "spoke<1>", "spoke<2>" }, tree.Children[0].Children.Select(n => n.DisplayName));
        Assert.Empty(tree.Children[1].Children);
        Assert.True(tree.Children[2].IsSuppressed);
    }

    [Fact]
    public void DrawingModelsShowTheirFileNames_AndOutsideFilesAreFlagged()
    {
        var tree = FileTree.Build(@"C:\CapstoneVault\a.slddrw", new[]
        {
            new RawComponent { Path = @"C:\Users\sam\Downloads\motor.sldprt" },
            new RawComponent { Path = @"C:\CapstoneVaultOld\x.sldprt" },
            new RawComponent { Path = @"C:\CapstoneVault\in.sldprt" },
        }, Vault);

        Assert.Equal(new[] { "in.sldprt", "motor.sldprt", "x.sldprt" }, tree.Children.Select(n => n.DisplayName));
        Assert.Equal(new[] { "motor.sldprt", "x.sldprt" }, tree.OutsideVault().Select(n => n.Name));
        Assert.Equal("in.sldprt", tree.Children[0].RelativePath);
    }
}
