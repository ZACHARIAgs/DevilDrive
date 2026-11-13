using System;
using System.Collections.Generic;
using System.Linq;
using Jarvis.Core.Vault;
using Xunit;

namespace Jarvis.Core.Tests;

/// <summary>Part numbering, McMaster-Carr parts and a new vault's starting folders.</summary>
public class FeaturesTests
{
    private static NumberingScheme Capstone() => new NumberingScheme
    {
        Prefix = "TLT",
        Digits = 3,
        Groups = new List<NumberGroup> { new NumberGroup { Number = 400, Name = "Bending fixture" }, new NumberGroup { Number = 200, Name = "Turret" } },
    };

    // ------------------------------------------------------------ part numbering

    [Fact]
    public void Numbering_SuggestsTheNextFreeNumberInAGroup()
    {
        var scheme = NumberingScheme.Parse(Capstone().Serialize())!;
        var turret = scheme.Groups.Single(g => g.Name == "Turret");
        var used = new[] { "TLT-201-Turret.SLDPRT", "TLT-202_5709K91_Tapered-Roller_Bearing.SLDPRT", "TLT-204_Spring.SLDPRT", "TLT-401_Bending_Base.SLDPRT", "notes.txt" };

        Assert.Equal(203, scheme.NextNumber(turret, used));
        Assert.Equal(402, scheme.NextNumber(scheme.Groups.Single(g => g.Number == 400), used));
        Assert.Equal("TLT-203", scheme.Format(203));
        Assert.Equal("TLT-203_Bearing_Housing", scheme.FileName(203, "bearing housing"));
        Assert.Equal("TLT-203", scheme.FileName(203, "  "));
        Assert.Equal(new[] { 200, 400 }, scheme.Groups.Select(g => g.Number)); // kept in order
    }

    [Fact]
    public void Numbering_KnowsWhichNamesFit()
    {
        var scheme = Capstone();

        Assert.True(scheme.Fits("TLT-203_Bearing_Housing.SLDPRT"));
        Assert.True(scheme.Fits("tlt-401-Base.sldprt"));
        Assert.False(scheme.Fits("Bearing Housing.SLDPRT"));
        Assert.False(scheme.Fits("TLT-301_Thing.SLDPRT"));   // no 300 group
        Assert.False(scheme.Fits("TLT-2031_Thing.SLDPRT"));  // four digits
        Assert.Equal(201, scheme.NumberIn("TLT-201"));
        Assert.Equal("Turret", scheme.GroupOf(250)!.Name);
        Assert.Equal((201, 299), scheme.RangeOf(scheme.Groups.Single(g => g.Number == 200)));
    }

    [Fact]
    public void Numbering_FullGroup_SaysSo()
    {
        var scheme = new NumberingScheme { Prefix = "A", Digits = 2, Groups = new List<NumberGroup> { new NumberGroup { Number = 10, Name = "Frame" } } };
        var used = Enumerable.Range(11, 9).Select(n => $"A-{n}_x.SLDPRT");

        var ex = Assert.Throws<Jarvis.Core.Errors.PdmException>(() => scheme.NextNumber(scheme.Groups[0], used));
        Assert.Contains("is full", ex.Message);
    }

    [Theory]
    [InlineData("TLT", 3, 200, "Turret", null)]
    [InlineData("TLT", 3, 1200, "Turret", "doesn't fit in 3 digits")]
    [InlineData("TL/T", 3, 200, "Turret", "can't contain")]
    [InlineData("TLT", 3, 200, " ", "Give group 200 a name")]
    [InlineData("TLT", 9, 200, "Turret", "2 to 6 digits")]
    public void Numbering_AdminWindowChecks(string prefix, int digits, int group, string name, string? problem)
    {
        var scheme = new NumberingScheme { Prefix = prefix, Digits = digits, Groups = new List<NumberGroup> { new NumberGroup { Number = group, Name = name } } };
        if (problem == null) Assert.Null(scheme.Problem());
        else Assert.Contains(problem, scheme.Problem());
    }

    [Fact]
    public void Numbering_NoSchemeOrNoGroups_IsNull()
    {
        Assert.Null(NumberingScheme.Parse(null));
        Assert.Null(NumberingScheme.Parse("{\"prefix\":\"TLT\",\"groups\":[]}"));
        Assert.Null(NumberingScheme.Parse("not json"));
        Assert.Equal("TLT-201_Turret_Plate.SLDPRT", Capstone().Example());
    }

    // ------------------------------------------------------------ McMaster-Carr

    [Theory]
    [InlineData("91251A540", "91251A540")]
    [InlineData(" 5709k91 ", "5709K91")]
    [InlineData("84935A511", "84935A511")]
    [InlineData("hello", null)]
    [InlineData("91251-A540", null)]
    public void McMaster_PartNumbers(string typed, string? normalized) => Assert.Equal(normalized, McMaster.Normalize(typed));

    [Fact]
    public void McMaster_FindsTheFinishedDownload()
    {
        var start = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var files = new[]
        {
            (@"C:\Users\x\Downloads\91251A540_Socket Head Screw.SLDPRT.crdownload", start.AddSeconds(5)), // still downloading
            (@"C:\Users\x\Downloads\91251A540_Socket Head Screw.STEP", start.AddSeconds(8)),
            (@"C:\Users\x\Downloads\91251A540_Socket Head Screw.SLDPRT", start.AddSeconds(6)),
            (@"C:\Users\x\Downloads\5709K91_Bearing.SLDPRT", start.AddSeconds(9)),                       // another part
            (@"C:\Users\x\Downloads\91251A540_old.SLDPRT", start.AddHours(-2)),                          // from before
        };

        Assert.Equal(@"C:\Users\x\Downloads\91251A540_Socket Head Screw.SLDPRT", McMaster.FindDownload(files, "91251A540", start));
        Assert.Null(McMaster.FindDownload(files.Take(1), "91251A540", start));
        Assert.Equal("COTS/MCMASTER/91251A540_Socket Head Screw.SLDPRT", McMaster.VaultPathFor(@"C:\Users\x\Downloads\91251A540_Socket Head Screw.SLDPRT"));
        Assert.Equal("https://www.mcmaster.com/91251A540/", McMaster.PageUrl("91251A540"));
    }

    // ------------------------------------------------------------ help

    [Theory]
    [InlineData("Conflict", "Conflict")]
    [InlineData("edited without check-out", "WITHOUT check-out")]
    [InlineData("File in use", "File in use")]
    [InlineData("backups", "backups")]
    public void Help_FindsTheTopicForAMessage(string search, string inTitle)
    {
        Assert.Contains(Jarvis.Core.Help.HelpTopics.All.Where(t => t.Matches(search)), t => t.Title.IndexOf(inTitle, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [Fact]
    public void Help_EveryTopicHasSteps()
    {
        Assert.All(Jarvis.Core.Help.HelpTopics.All, t => Assert.NotEmpty(t.Steps));
        Assert.DoesNotContain(Jarvis.Core.Help.HelpTopics.All, t => t.Matches("zzzz qqqq"));
    }

    // ------------------------------------------------------------ joining steps on the vault's GitHub page

    [Fact]
    public void NewVault_GitHubPage_ShowsHowToJoin()
    {
        var files = VaultCreator.TemplateFiles("Club Vault", @"C:\ClubVault", 2024);

        var readme = files[".github/README.md"];
        Assert.StartsWith("# Club Vault", readme);
        Assert.Contains("Install DevilDrive", readme);
        Assert.Contains("Accept and download", readme);
        Assert.Contains(@"`C:\ClubVault`", readme);
    }

    [Fact]
    public void JoiningSteps_LinkTheLatestJarvis()
    {
        var readme = VaultInvites.Readme("Club Vault", @"C:\ClubVault", VaultInvites.InstallLink("team/jarvis"));
        Assert.Contains("[DevilDrive.exe](https://github.com/team/jarvis/releases/latest/download/DevilDrive.exe)", readme);
        Assert.Contains("Ask an admin for DevilDrive.exe", VaultInvites.Readme("Club Vault", null, null));
    }

    // ------------------------------------------------------------ starting folders

    [Fact]
    public void NewVault_StartsWithCotsAndSandbox_AsHiddenPlaceholders()
    {
        var request = new NewVaultRequest();
        var files = VaultCreator.TemplateFiles("Club Vault", @"C:\ClubVault", 2024, request.Folders);

        Assert.Equal(new[] { "COTS", "Sandbox" }, request.Folders);
        Assert.Contains("COTS/.keep", files.Keys);
        Assert.Contains("Sandbox/.keep", files.Keys);
        Assert.DoesNotContain(files.Keys, k => k.EndsWith(".keep") && !k.StartsWith("COTS") && !k.StartsWith("Sandbox"));
        Assert.DoesNotContain(VaultCreator.TemplateFiles("Club Vault", @"C:\ClubVault", 2024).Keys, k => k.EndsWith(".keep"));
    }

    [Fact]
    public void NewVault_ChecksStartingFolderNames()
    {
        var request = new NewVaultRequest { VaultName = "Club Vault", Owner = "club", RepoName = "club-vault", LocalPath = @"C:\NoSuchFolderForTests\ClubVault" };
        Assert.Null(VaultCreator.Validate(request));

        request.Folders = new List<string> { "COTS", "cots" };
        Assert.Contains("same name", VaultCreator.Validate(request));
        request.Folders = new List<string> { "Bad:Name" };
        Assert.Contains("Starting folder \"Bad:Name\"", VaultCreator.Validate(request));
    }

    [Fact]
    public void Numbering_NameIsNumber_OneSequence_FileNameIsTheNumber()
    {
        var scheme = NumberingScheme.Parse("{ \"prefix\": \"TLT\", \"digits\": 3, \"nameIsNumber\": true, \"cotsFolder\": \"COTS\" }")!;

        Assert.NotNull(scheme);
        Assert.Null(scheme.Problem());
        var group = Assert.Single(scheme.Groups);
        Assert.Equal(1, scheme.NextNumber(group, new[] { "Old Name.SLDPRT" }));
        Assert.Equal(3, scheme.NextNumber(group, new[] { "TLT-001.SLDPRT", "TLT-002" }));
        Assert.Equal(1000 - 1, scheme.RangeOf(group).Last);
        Assert.Equal("TLT-007", scheme.FileName(7, "Bearing housing"));
        Assert.True(scheme.Fits("TLT-640.SLDASM"));
        Assert.Equal("TLT-001.SLDPRT", scheme.Example());
    }

    [Theory]
    [InlineData("COTS/MCMASTER/91251A540_Screw.SLDPRT", true, true)]
    [InlineData("91251A540_Socket Head Screw.SLDPRT", false, true)]
    [InlineData("5709K77_Tapered-Roller Bearing.SLDPRT", false, true)]
    [InlineData("TLT-201-Turret.SLDPRT", false, false)]
    [InlineData("bearing_block.SLDPRT", false, false)]
    [InlineData("Cotsy/part.SLDPRT", false, false)]
    public void Numbering_NameIsNumber_Cots(string path, bool isCots, bool looksBought)
    {
        var scheme = NumberingScheme.Parse("{ \"prefix\": \"TLT\", \"digits\": 3, \"nameIsNumber\": true, \"cotsFolder\": \"COTS\" }")!;
        Assert.Equal(isCots, scheme.IsCots(path));
        Assert.Equal(looksBought, scheme.LooksBought(path));
    }

    [Fact]
    public void Numbering_NameIsNumber_NeedsAPrefix()
    {
        var scheme = new NumberingScheme { Prefix = "", NameIsNumber = true };
        Assert.Contains("prefix", scheme.Problem());
    }

    [Theory]
    [InlineData("Turret/plate.SLDPRT", "Clamp Plate", "Turret/Clamp Plate.SLDPRT")]
    [InlineData("Turret/plate.SLDPRT", " Clamp Plate.sldprt ", "Turret/Clamp Plate.SLDPRT")]
    [InlineData("plate.SLDPRT", "TLT-1.5 plate", "TLT-1.5 plate.SLDPRT")]
    public void Rename_KeepsTheFolderAndExtension(string path, string typed, string expected)
    {
        Assert.Equal(expected, VaultService.RenamedPath(path, typed));
        Assert.Null(VaultService.FileNameProblem(path, typed));
    }

    [Theory]
    [InlineData("", "Type a new name")]
    [InlineData(".SLDPRT", "Type a new name")]
    [InlineData("a:b", "can't contain")]
    [InlineData("sub/name", "can't contain")]
    [InlineData("plate", "its name already")]
    [InlineData("Plate", "capital letters")]
    public void Rename_Problems(string typed, string expected)
    {
        Assert.Contains(expected, VaultService.FileNameProblem("Turret/plate.SLDPRT", typed));
    }
}
