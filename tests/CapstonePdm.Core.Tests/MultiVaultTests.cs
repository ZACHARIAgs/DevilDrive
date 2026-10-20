using System.Collections.Generic;
using CapstonePdm.Core.Config;
using CapstonePdm.Core.Vault;
using Xunit;

namespace CapstonePdm.Core.Tests;

public class MultiVaultTests
{
    private static UserSettings Settings(string current, params string[] vaults)
    {
        var settings = new UserSettings { VaultPath = current, Vaults = new List<string>(vaults) };
        settings.Normalize();
        return settings;
    }

    [Fact]
    public void VaultFor_PicksTheVaultHoldingTheFile()
    {
        var settings = Settings(@"C:\CapstoneVault", @"C:\ClubVault\", @"c:\capstonevault");

        Assert.Equal(new[] { @"C:\ClubVault", @"C:\CapstoneVault" }, settings.Vaults);
        Assert.Equal(@"C:\ClubVault", settings.VaultFor(@"C:\ClubVault\frame\rail.SLDPRT"));
        Assert.Equal(@"C:\CapstoneVault", settings.VaultFor(@"C:\CapstoneVault\TEST ASSEMBLY.SLDASM"));
        Assert.Null(settings.VaultFor(@"C:\CapstoneVaultOld\x.sldprt"));
        Assert.Null(settings.VaultFor(@"C:\Users\sam\Downloads\motor.sldprt"));
    }

    [Fact]
    public void RemovingTheCurrentVault_SwitchesToAnother()
    {
        var settings = Settings(@"C:\CapstoneVault", @"C:\ClubVault");
        settings.RemoveVault(@"C:\CapstoneVault\");

        Assert.Equal(@"C:\ClubVault", settings.VaultPath);
        Assert.Equal(new[] { @"C:\ClubVault" }, settings.Vaults);
    }

    [Fact]
    public void NewSettings_ListTheDefaultVault()
    {
        var settings = new UserSettings();
        settings.Normalize();
        Assert.Equal(new[] { UserSettings.DefaultVaultPath }, settings.Vaults);
    }

    [Theory]
    [InlineData("frame/bracket.SLDPRT", 3, "frame/_versions/bracket v3.SLDPRT")]
    [InlineData("top.sldasm", 12, "_versions/top v12.sldasm")]
    public void VersionCopy_Paths(string original, int version, string copy)
    {
        Assert.Equal(copy, VersionCopies.CopyPathFor(original, version));
        Assert.Equal(original, VersionCopies.OriginalOf(copy));
        Assert.Equal(version, VersionCopies.VersionOf(copy));
    }

    [Fact]
    public void VersionCopy_RecognizesFullPaths_AndIgnoresOtherFiles()
    {
        Assert.Equal(@"C:\V\frame\bracket v2 final.SLDPRT", VersionCopies.OriginalOf(@"C:\V\frame\_versions\bracket v2 final v4.SLDPRT"));
        Assert.False(VersionCopies.IsVersionCopy("frame/bracket v3.SLDPRT"));
        Assert.False(VersionCopies.IsVersionCopy("_versions/notes.txt"));
    }

    [Theory]
    [InlineData("https://github.com/team/club-vault.git", "https://github.com/Team/club-vault", true)]
    [InlineData("https://github.com/team/club-vault/", "https://github.com/team/club-vault.git", true)]
    [InlineData("https://github.com/team/club-vault", "https://github.com/team/capstone-vault", false)]
    public void SameRepo_IgnoresDotGitCaseAndSlash(string a, string b, bool same) =>
        Assert.Equal(same, VaultCatalog.SameRepo(a, b));
}
