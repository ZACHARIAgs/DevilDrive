using System.Collections.Generic;
using System.Linq;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Git;
using CapstonePdm.Core.Notifications;
using CapstonePdm.Core.Vault;
using Xunit;

namespace CapstonePdm.Core.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\path with space\", "\"C:\\path with space\\\\\"")]
    [InlineData(@"C:\no\space", @"C:\no\space")]
    public void Quote_FollowsWindowsRules(string input, string expected) =>
        Assert.Equal(expected, CommandLine.Quote(input));
}

public class GitErrorClassifierTests
{
    private static GitResult Fail(string stderr) => new GitResult(new[] { "push" }, 1, "", stderr);

    [Fact]
    public void NoNetwork_IsOffline() =>
        Assert.IsType<VaultOfflineException>(GitErrorClassifier.Classify(
            Fail("fatal: unable to access 'https://github.com/team/vault.git/': Could not resolve host: github.com"), "check in"));

    [Fact]
    public void Http403_IsAuth_NotOffline() =>
        Assert.IsType<VaultAuthException>(GitErrorClassifier.Classify(
            Fail("fatal: unable to access 'https://github.com/team/vault.git/': The requested URL returned error: 403"), "check in"));

    [Fact]
    public void NonFastForward_IsOutOfDate()
    {
        var error = GitErrorClassifier.Classify(Fail(
            " ! [rejected]        HEAD -> main (fetch first)\nerror: failed to push some refs to 'https://github.com/team/vault.git'"), "check in");
        Assert.Equal(PushRejectReason.OutOfDate, Assert.IsType<PushRejectedException>(error).Reason);
    }

    [Fact]
    public void LockedFiles_ListsThem()
    {
        var error = GitErrorClassifier.Classify(Fail(
            "Unable to push locked files:\n* Parts/bracket.SLDPRT - sam\nERROR: Cannot update locked files.\nerror: failed to push some refs"), "check in");
        var rejected = Assert.IsType<PushRejectedException>(error);
        Assert.Equal(PushRejectReason.LockedByOthers, rejected.Reason);
        Assert.Contains("Parts/bracket.SLDPRT - sam", rejected.Message);
    }

    [Fact]
    public void FileOpenInSolidWorks_IsInUse()
    {
        var error = GitErrorClassifier.Classify(Fail(
            "error: unable to unlink old 'Parts/bracket.SLDPRT': Invalid argument"), "get the latest versions");
        var inUse = Assert.IsType<FileInUseException>(error);
        Assert.Equal("Parts/bracket.SLDPRT", inUse.Paths.Single());
    }

    [Fact]
    public void Unknown_KeepsGitMessage()
    {
        var error = GitErrorClassifier.Classify(Fail("fatal: something odd happened"), "check in");
        Assert.IsType<GitCommandException>(error);
        Assert.Contains("something odd happened", error.Message);
        Assert.Contains("git push", error.Detail);
    }

    [Fact]
    public void NotSent_ExplainsWorkIsSafe()
    {
        var wrapped = PushRejectedException.NotSent(new VaultOfflineException("check in"));
        Assert.Contains("saved on this PC", wrapped.Message);
        Assert.Contains("still checked out", wrapped.Message);
    }
}

public class GitParsersTests
{
    [Fact]
    public void ParseStatus_HandlesModifiedUntrackedAndRenames()
    {
        var entries = GitParsers.ParseStatus(" M Parts/a.SLDPRT\0?? new part.SLDPRT\0R  b.txt\0old b.txt\0 D gone.pdf\0");
        Assert.Equal(new[] { "Parts/a.SLDPRT", "new part.SLDPRT", "b.txt", "gone.pdf" }, entries.Select(e => e.Path));
        Assert.True(entries[1].IsUntracked);
        Assert.True(entries[3].IsDeleted);
    }

    [Fact]
    public void ParseCheckAttr_ReadsTriples()
    {
        var values = GitParsers.ParseCheckAttr("a.SLDPRT\0lockable\0set\0notes.txt\0lockable\0unspecified\0");
        Assert.Equal(("a.SLDPRT", "set"), values[0]);
        Assert.Equal(("notes.txt", "unspecified"), values[1]);
    }

    [Fact]
    public void ParseVerifiedLocks_SplitsOursAndTheirs()
    {
        const string json = """
            {"ours":[{"id":"1","path":"a.SLDPRT","owner":{"name":"me"},"locked_at":"2026-10-19T14:00:00Z"}],
             "theirs":[{"id":"2","path":"b.SLDASM","owner":{"name":"sam"},"locked_at":"2026-10-18T09:30:00Z"}]}
            """;
        var locks = GitParsers.ParseVerifiedLocks(json);
        Assert.Equal(2, locks.Count);
        Assert.True(locks.Single(l => l.Path == "a.SLDPRT").IsMine);
        var theirs = locks.Single(l => l.Path == "b.SLDASM");
        Assert.False(theirs.IsMine);
        Assert.Equal("sam", theirs.Owner);
        Assert.Equal(2026, theirs.LockedAt!.Value.Year);
    }

    [Fact]
    public void ParseLocks_UsesLoginForOwnership()
    {
        var locks = GitParsers.ParseLocks("""[{"id":"7","path":"x.pdf","owner":{"name":"Me"}}]""", "me");
        Assert.True(locks.Single().IsMine);
    }

    [Fact]
    public void ParseHistory_NumbersOldestAsVersionOne()
    {
        var output = "\u001e" + "bbb\u001fAlex\u001fa@x\u001f2026-10-19T10:00:00+00:00\u001fadded holes\n\n" +
                     "\u001e" + "aaa\u001fSam\u001fs@x\u001f2026-10-18T10:00:00+00:00\u001ffirst version\nmore detail\n";
        var versions = GitParsers.ParseHistory(output);
        Assert.Equal(new[] { 2, 1 }, versions.Select(v => v.Version));
        Assert.Equal("added holes", versions[0].Comment);
        Assert.Equal("first version", versions[1].CommentFirstLine);
    }

    [Fact]
    public void ParseLatestVersions_CountsCheckInsPerFile()
    {
        var output = "\u001e" + "c3\u001fAlex\u001fa@x\u001f2026-10-19T10:00:00+00:00\u001fthird\n\na.SLDPRT\n" +
                     "\u001e" + "c2\u001fSam\u001fs@x\u001f2026-10-18T10:00:00+00:00\u001fsecond\n\na.SLDPRT\nb.SLDPRT\n" +
                     "\u001e" + "c1\u001fSam\u001fs@x\u001f2026-10-17T10:00:00+00:00\u001ffirst\n\na.SLDPRT\n";
        var latest = GitParsers.ParseLatestVersions(output);
        Assert.Equal(3, latest["a.SLDPRT"].Version);
        Assert.Equal("third", latest["a.SLDPRT"].Comment);
        Assert.Equal(1, latest["b.SLDPRT"].Version);
        Assert.Equal("Sam", latest["b.SLDPRT"].Author);
    }
}

public class WatchListTests
{
    [Fact]
    public void WatchedFile_IsReportedOnce_WhenItBecomesFree()
    {
        var vault = "C:\\TestVault-" + System.Guid.NewGuid().ToString("N");
        try
        {
            CapstonePdm.Core.Config.WatchList.Watch(vault, "Parts/bracket.sldprt");
            Assert.True(CapstonePdm.Core.Config.WatchList.IsWatching(vault, "parts/BRACKET.sldprt"));

            var stillTaken = new VaultSnapshot { Files = new[] { new VaultFile { RelativePath = "Parts/bracket.sldprt", State = FileState.CheckedOutByOther } } };
            Assert.Empty(CapstonePdm.Core.Config.WatchList.TakeNowFree(vault, stillTaken));

            var free = new VaultSnapshot { Files = new[] { new VaultFile { RelativePath = "Parts/bracket.sldprt", State = FileState.Available } } };
            Assert.Single(CapstonePdm.Core.Config.WatchList.TakeNowFree(vault, free));
            Assert.Empty(CapstonePdm.Core.Config.WatchList.TakeNowFree(vault, free));
            Assert.False(CapstonePdm.Core.Config.WatchList.IsWatching(vault, "Parts/bracket.sldprt"));
        }
        finally
        {
            CapstonePdm.Core.Config.WatchList.Stop(vault, "Parts/bracket.sldprt");
        }
    }
}

public class RevisionTests
{
    [Theory]
    [InlineData(null, "A")]
    [InlineData("", "A")]
    [InlineData("A", "B")]
    [InlineData("H", "J")]   // I is skipped
    [InlineData("N", "P")]   // O is skipped
    [InlineData("P", "R")]   // Q is skipped
    [InlineData("Y", "AA")]
    [InlineData("AY", "BA")]
    [InlineData("b", "C")]
    public void Next_FollowsDrawingLetterRules(string? current, string expected) =>
        Assert.Equal(expected, Revisions.Next(current));
}

public class DiscordMessageTests
{
    [Fact]
    public void CheckIn_IncludesVersionAndQuotedComment()
    {
        var e = new VaultEvent(VaultEventKind.CheckedIn, "Alex", new[] { "Parts/bracket.SLDPRT" })
        {
            Comment = "added *mounting* holes\nand fillets",
            Versions = new Dictionary<string, int> { ["Parts/bracket.SLDPRT"] = 5 },
        };
        var message = DiscordWebhookNotifier.BuildMessage(e);
        Assert.Contains("**Alex** checked in `Parts/bracket.SLDPRT` (v5)", message);
        Assert.Contains("> added \\*mounting\\* holes\n> and fillets", message);
    }

    [Fact]
    public void ManyFiles_AreSummarized()
    {
        var files = Enumerable.Range(1, 14).Select(i => $"p{i}.SLDPRT").ToList();
        var message = DiscordWebhookNotifier.BuildMessage(new VaultEvent(VaultEventKind.CheckedOut, "Sam", files));
        Assert.Contains("and 4 more", message);
    }
}

public class BomTests
{
    [Fact]
    public void Csv_UsesPartNumberOrFileName_QuotesCommas_AndDefusesFormulas()
    {
        var lines = new List<BomLine>
        {
            new BomLine { Path = @"C:\V\bolt.SLDPRT", Configuration = "M6", Quantity = 8,
                Properties = new Dictionary<string, string> { ["PartNo"] = "100-02", ["Description"] = "Bolt, hex" } },
            new BomLine { Path = @"C:\V\plate.SLDPRT", Configuration = "Default", Quantity = 1, Revision = "B",
                Properties = new Dictionary<string, string> { ["Description"] = "=HYPERLINK(1)" } },
        };

        var rows = Bom.ToCsv(lines).Split('\n').Select(r => r.TrimEnd('\r')).Where(r => r.Length > 0).ToList();

        Assert.Equal("Item,Part Number,Description,Material,Qty,Revision,Configuration,File", rows[0]);
        Assert.Equal("1,100-02,\"Bolt, hex\",,8,,M6,bolt.SLDPRT", rows[1]);
        Assert.Equal("2,plate,'=HYPERLINK(1),,1,B,Default,plate.SLDPRT", rows[2]);
    }
}

public class UpdaterTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0.0")]
    [InlineData("0.2", "0.2.0.0")]
    [InlineData("1.4.2+abc123", "1.4.2.0")]
    [InlineData("v1.0.0-beta", "1.0.0.0")]
    public void ParseVersion_HandlesTagsAndBuildSuffixes(string text, string expected) =>
        Assert.Equal(System.Version.Parse(expected), CapstonePdm.Core.Updates.Updater.ParseVersion(text));

    [Fact]
    public void ParseVersion_RejectsNonVersions() => Assert.Null(CapstonePdm.Core.Updates.Updater.ParseVersion("latest"));

    [Fact]
    public void ParseChecksums_ReadsSha256sumFormat()
    {
        var hash = new string('a', 64);
        var sums = CapstonePdm.Core.Updates.Updater.ParseChecksums($"{hash}  CapstonePDM.exe\r\n{new string('b', 64)} *Setup.zip\njunk line\n");
        Assert.Equal(hash, sums["capstonepdm.exe"]);
        Assert.Equal(new string('b', 64), sums["Setup.zip"]);
        Assert.Equal(2, sums.Count);
    }
}

public class CommitCountTests
{
    [Fact]
    public void CountCommitsPerFile_CountsEachCommitThatTouchedAFile()
    {
        var output = "\x1e\n\na.sldprt\nb.sldasm\n\x1e\n\na.sldprt\n";
        var counts = GitParsers.CountCommitsPerFile(output);
        Assert.Equal(2, counts["A.SLDPRT"]);
        Assert.Equal(1, counts["b.sldasm"]);
    }
}

public class FileTreeTests
{
    private const string Vault = @"C:\CapstoneVault";

    private static RawComponent C(string path, bool suppressed = false, string config = "Default", params RawComponent[] children) =>
        new RawComponent { Path = path, IsSuppressed = suppressed, Configuration = config, Children = children.ToList() };

    [Fact]
    public void GroupsRepeatedParts_KeepsSuppressedSeparate_AndShowsSubassembliesOnce()
    {
        var wheel = C(@"C:\CapstoneVault\cart\wheel.sldasm", false, "Default",
            C(@"C:\CapstoneVault\cart\hub.sldprt"), C(@"C:\CapstoneVault\cart\spoke.sldprt"), C(@"C:\CapstoneVault\cart\spoke.sldprt"));
        var tree = FileTree.Build(@"C:\CapstoneVault\cart\cart.sldasm", new[]
        {
            C(@"C:\CapstoneVault\cart\bolt.sldprt"), C(@"C:\CapstoneVault\cart\bolt.sldprt"), C(@"C:\CapstoneVault\cart\BOLT.SLDPRT"),
            C(@"C:\CapstoneVault\cart\bolt.sldprt", suppressed: true),
            wheel, wheel,
        }, Vault);

        Assert.Equal("cart/cart.sldasm", tree.RelativePath);
        Assert.Equal(new[] { "wheel.sldasm", "bolt.sldprt", "bolt.sldprt" }, tree.Children.Select(n => n.Name.ToLowerInvariant()));
        var wheelNode = tree.Children[0];
        Assert.Equal(2, wheelNode.Quantity);
        Assert.Equal(2, wheelNode.Children.Single(n => n.Name == "spoke.sldprt").Quantity);
        Assert.Equal(3, tree.Children[1].Quantity);
        Assert.False(tree.Children[1].IsSuppressed);
        Assert.True(tree.Children[2].IsSuppressed);
        Assert.Equal(1, tree.Children[2].Quantity);
    }

    [Fact]
    public void DifferentConfigurationsAreSeparateRows()
    {
        var tree = FileTree.Build(@"C:\CapstoneVault\a.sldasm", new[]
        {
            C(@"C:\CapstoneVault\bolt.sldprt", config: "M6"), C(@"C:\CapstoneVault\bolt.sldprt", config: "M8"),
        }, Vault);
        Assert.Equal(new[] { "M6", "M8" }, tree.Children.Select(n => n.Configuration));
    }

    [Fact]
    public void FlagsFilesOutsideTheVault()
    {
        var tree = FileTree.Build(@"C:\CapstoneVault\a.sldasm", new[]
        {
            C(@"C:\Users\sam\Downloads\motor.sldprt"), C(@"C:\CapstoneVaultOld\x.sldprt"), C(@"C:\CapstoneVault\in.sldprt"),
        }, Vault);
        Assert.Equal(new[] { "motor.sldprt", "x.sldprt" }, tree.OutsideVault().Select(n => n.Name).OrderBy(n => n));
        Assert.Equal("in.sldprt", tree.Children.Single(n => !n.IsOutsideVault).RelativePath);
        Assert.Equal(4, tree.SelfAndDescendants().Count());
    }
}
