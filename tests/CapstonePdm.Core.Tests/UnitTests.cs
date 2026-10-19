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
