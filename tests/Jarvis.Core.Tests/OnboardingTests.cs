using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jarvis.Core.Errors;
using Jarvis.Core.Git;
using Jarvis.Core.Install;
using Xunit;
using static Jarvis.Core.Tests.NewTeamTests;

namespace Jarvis.Core.Tests;

/// <summary>Getting a new teammate going: installing Jarvis.exe, Git's name and email, GitHub invitations.</summary>
public class OnboardingTests : IDisposable
{
    private static readonly string CR = ((char)13).ToString();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "capstone-pdm-tests", Guid.NewGuid().ToString("N"));

    public OnboardingTests() => Directory.CreateDirectory(_root);

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

    private InstallLayout Layout() =>
        new InstallLayout(Path.Combine(_root, "Programs", "DevilDrive"), new[] { Path.Combine(_root, "Desktop"), Path.Combine(_root, "Start Menu") }, isTest: true);

    // ------------------------------------------------------------ install

    [Fact]
    public void Install_KnowsWhetherItRunsFromTheInstallFolder()
    {
        var layout = Layout();
        Assert.True(AppInstaller.IsInstalled(Path.Combine(_root, "programs", "DEVILDRIVE", "devildrive.exe"), layout));
        Assert.False(AppInstaller.IsInstalled(Path.Combine(_root, "Downloads", "DevilDrive.exe"), layout));
        Assert.Null(AppInstaller.InstalledVersion(layout));
    }

    [Fact]
    public void Install_TestRootKeepsEverythingInsideIt()
    {
        var before = Environment.GetEnvironmentVariable(InstallLayout.TestRootVariable);
        try
        {
            Environment.SetEnvironmentVariable(InstallLayout.TestRootVariable, _root);
            var layout = InstallLayout.ForThisUser();
            Assert.True(layout.IsTest);
            Assert.Equal(Path.Combine(_root, "Programs", "DevilDrive", "DevilDrive.exe"), layout.ExePath);
            Assert.All(layout.ShortcutFolders, f => Assert.StartsWith(_root, f));
        }
        finally
        {
            Environment.SetEnvironmentVariable(InstallLayout.TestRootVariable, before);
        }
        Assert.False(InstallLayout.ForThisUser().IsTest);
        Assert.EndsWith(Path.Combine("Programs", "DevilDrive", "DevilDrive.exe"), InstallLayout.ForThisUser().ExePath);
    }

    [Fact]
    public void Install_CopiesTheExeAndClearsTheDownloadMark()
    {
        var layout = Layout();
        var download = Path.Combine(_root, "Downloads", "DevilDrive.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(download)!);
        File.WriteAllText(download, "new");
        File.WriteAllText(download + ":Zone.Identifier", "[ZoneTransfer]" + Environment.NewLine + "ZoneId=3");

        AppInstaller.CopyApp(download, layout);

        Assert.Equal("new", File.ReadAllText(layout.ExePath));
        Assert.False(File.Exists(layout.ExePath + ":Zone.Identifier"));
        Assert.True(File.Exists(download + ":Zone.Identifier")); // the download itself is left alone
    }

    [Fact]
    public void Install_ReplacesARunningCopy()
    {
        var layout = Layout();
        Directory.CreateDirectory(layout.InstallFolder);
        File.WriteAllText(layout.ExePath, "old");
        var download = Path.Combine(_root, "DevilDrive.exe");
        File.WriteAllText(download, "new");

        // A running exe can't be deleted or overwritten, only renamed: a shared-delete handle behaves the same.
        using (new FileStream(layout.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            AppInstaller.CopyApp(download, layout);
        }

        Assert.Equal("new", File.ReadAllText(layout.ExePath));
    }

    [Fact]
    public void Install_AddsShortcutsToTheInstalledExe()
    {
        var layout = Layout();
        Directory.CreateDirectory(layout.InstallFolder);
        File.WriteAllText(layout.ExePath, "");

        var shortcuts = AppInstaller.CreateShortcuts(layout);
        AppInstaller.CreateShortcuts(layout); // again: replaced, not duplicated

        Assert.Equal(new[] { Path.Combine(_root, "Desktop", "DevilDrive.lnk"), Path.Combine(_root, "Start Menu", "DevilDrive.lnk") }, shortcuts);
        Assert.All(shortcuts, s => Assert.True(File.Exists(s)));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "Desktop")));
    }

    [Fact]
    public void Winget_SilentInstallArguments()
    {
        var user = AppInstaller.WingetArguments(AppInstaller.GitPackage, userScope: true);
        Assert.Contains("--id Git.Git --exact", user);
        Assert.Contains("--silent", user);
        Assert.Contains("--accept-package-agreements", user);
        Assert.EndsWith("--scope user", user);
        Assert.DoesNotContain("--scope", AppInstaller.WingetArguments(AppInstaller.GitLfsPackage, userScope: false));
    }

    [Fact]
    public void Winget_OutputBecomesReadableProgress()
    {
        Assert.Null(AppInstaller.ParseWingetOutput(""));
        Assert.Null(AppInstaller.ParseWingetOutput(CR + "   -" + CR + "   \\" + CR + "   |"));

        var found = AppInstaller.ParseWingetOutput("Found Git [Git.Git] Version 2.51.0" + Environment.NewLine + CR + "   /");
        Assert.Equal("Found Git [Git.Git] Version 2.51.0", found!.Text);
        Assert.Null(found.Percent);

        var bar = new string((char)0x2588, 10) + new string((char)0x2592, 20);
        var downloading = AppInstaller.ParseWingetOutput("Downloading https://github.com/git.exe" + Environment.NewLine +
                                                         CR + "  " + bar + "  12.0 MB / 60.0 MB" + CR + "  " + bar + "  30.0 MB / 60.0 MB");
        Assert.Equal(50, downloading!.Percent!.Value, 1);
        Assert.StartsWith("Downloading (30 of 60 MB)", downloading.Text);

        var kb = AppInstaller.ParseWingetOutput("  " + bar + "  512 KB / 2.00 MB");
        Assert.Equal(25, kb!.Percent!.Value, 1);

        var percent = AppInstaller.ParseWingetOutput(CR + "  " + bar + "  40%");
        Assert.Equal(40, percent!.Percent);

        Assert.Equal("Successfully installed", AppInstaller.ParseWingetOutput("Starting package install..." + Environment.NewLine + "Successfully installed" + Environment.NewLine)!.Text);
    }

    // ------------------------------------------------------------ name and email

    [Fact]
    public void Identity_SuggestsProfileNameAndNoReplyEmail()
    {
        var named = GitIdentity.Suggest(new GitHubUser("zoe-p", 4242, "Zoe Park"));
        Assert.Equal("Zoe Park", named.Name);
        Assert.Equal("4242+zoe-p@users.noreply.github.com", named.Email);
        Assert.Equal("zoe-p", GitIdentity.Suggest(new GitHubUser("zoe-p", 4242, "  ")).Name);
    }

    [Theory]
    [InlineData("Zoe Park", "zoe@example.com", null)]
    [InlineData("", "zoe@example.com", "Type your name")]
    [InlineData("Zoe", " ", "Type an email")]
    [InlineData("Zoe", "zoe", "isn't an email")]
    [InlineData("Zoe", "zoe@", "isn't an email")]
    [InlineData("Zoe", "zo e@example.com", "isn't an email")]
    public void Identity_Validate(string name, string email, string? problem)
    {
        var result = GitIdentity.Validate(name, email);
        if (problem == null) Assert.Null(result);
        else Assert.Contains(problem, result);
    }

    [Fact]
    public async Task Identity_OnlyFillsWhatIsMissing()
    {
        var git = new GitRunner(_root, GitRunner.FindGit() ?? throw new InvalidOperationException("Git is needed for this test."));
        var config = Path.Combine(_root, "gitconfig");
        File.WriteAllText(config, "");
        var scope = new[] { "--file", config };

        var empty = await GitIdentity.GetAsync(git, scope, default);
        Assert.False(empty.IsComplete);
        Assert.Null(empty.Name);

        await git.RunAsync(new[] { "config", "--file", config, "user.email", "zoe@school.edu" });
        var filled = await GitIdentity.SetIfMissingAsync(git, scope, " Zoe Park ", "4242+zoe-p@users.noreply.github.com", default);

        Assert.True(filled.IsComplete);
        Assert.Equal("Zoe Park", filled.Name);
        Assert.Equal("zoe@school.edu", filled.Email); // already set: kept

        var again = await GitIdentity.SetIfMissingAsync(git, scope, "Someone Else", "x@y.z", default);
        Assert.Equal("Zoe Park", again.Name);
    }

    [Fact]
    public void UpdatePrompt_ShowsOnlyWhatsNew()
    {
        var nl = Environment.NewLine;
        var notes = "## To install: download **DevilDrive.exe** below and open it" + nl + nl +
                    "Windows may warn you: click **More info**, then **Run anyway**." + nl + nl +
                    "You don't need the other files: Jarvis uses them for updates." + nl + nl +
                    "What's new: version numbers in the app." + nl + nl +
                    "**SHA-256 checksums**" + nl + "```" + nl + "abc  DevilDrive.exe" + nl + "```";

        Assert.Equal("What's new: version numbers in the app.", Jarvis.Core.Updates.Updater.WhatsNew(notes));
        Assert.Equal("Older notes", Jarvis.Core.Updates.Updater.WhatsNew("Older notes" + nl + nl + "**SHA-256 checksums**" + nl + "x"));
        Assert.EndsWith("\u2026", Jarvis.Core.Updates.Updater.WhatsNew(new string('a', 700)));
    }

    // ------------------------------------------------------------ vault folder

    [Theory]
    [InlineData(@"C:\ClubVault", @"C:\ClubVault")]
    [InlineData(@"C:\ClubVault", @"c:\clubvault\")]
    [InlineData("C:/ClubVault", @"C:\ClubVault")]
    [InlineData(null, @"D:\Anywhere")]
    [InlineData("  ", @"D:\Anywhere")]
    public void FolderWarning_NoneWhenTheVaultIsWhereTheTeamKeepsIt(string? localPath, string vaultRoot) =>
        Assert.Null(new Config.VaultConfig { LocalPath = localPath }.FolderWarning(vaultRoot));

    [Fact]
    public void FolderWarning_SaysWhereToMoveIt()
    {
        var warning = new Config.VaultConfig { LocalPath = @"C:\ClubVault" }.FolderWarning(@"D:\Stuff\ClubVault\");
        Assert.NotNull(warning);
        Assert.StartsWith(@"This vault is in D:\Stuff\ClubVault, but your team keeps it in C:\ClubVault.", warning);
        Assert.Contains("missing parts", warning);
        Assert.Contains(@"move the folder to C:\ClubVault", warning);
    }

    // ------------------------------------------------------------ invitations

    private const string InvitationsJson =
        "[{\"id\":11,\"repository\":{\"full_name\":\"club/club-vault\",\"clone_url\":\"https://github.com/club/club-vault.git\",\"description\":\"Jarvis vault: Club Vault\"}," +
        "\"inviter\":{\"login\":\"lead\"},\"expired\":false}," +
        "{\"id\":12,\"repository\":{\"full_name\":\"sam/homework\",\"description\":null},\"inviter\":{\"login\":\"sam\"},\"expired\":false}," +
        "{\"id\":13,\"repository\":{\"full_name\":\"old/vault\",\"description\":\"\"},\"inviter\":{\"login\":\"old\"},\"expired\":true}]";

    [Fact]
    public async Task Invitations_ListsWaitingOnesNotExpired()
    {
        var fake = new FakeGitHub((_, _) => Reply(HttpStatusCode.OK, InvitationsJson));
        var invitations = await new GitHubApi("token", new HttpClient(fake)).GetMyInvitationsAsync();

        Assert.Equal(new long[] { 11, 12 }, invitations.Select(i => i.Id));
        Assert.Equal("/user/repository_invitations?per_page=100", fake.Requests.Single().Path);
        var vault = invitations[0];
        Assert.Equal("club/club-vault", vault.RepoFullName);
        Assert.Equal("lead", vault.Inviter);
        Assert.True(vault.LooksLikeVault);
        Assert.Equal("https://github.com/sam/homework.git", invitations[1].CloneUrl); // no clone_url: made from the name
        Assert.False(invitations[1].LooksLikeVault);
    }

    [Fact]
    public async Task Invitations_Accept()
    {
        var fake = new FakeGitHub((_, _) => Reply(HttpStatusCode.NoContent));
        var invitation = new GitHubRepoInvitation(11, "club/club-vault", "https://github.com/club/club-vault.git", null, "lead", false);

        await new GitHubApi("token", new HttpClient(fake)).AcceptInvitationAsync(invitation);

        var request = fake.Requests.Single();
        Assert.Equal("PATCH", request.Method.Method);
        Assert.Equal("/user/repository_invitations/11", request.Path);
    }

    [Fact]
    public async Task Invitations_AcceptOneThatIsGone()
    {
        var fake = new FakeGitHub((_, _) => Reply(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"));
        var invitation = new GitHubRepoInvitation(11, "club/club-vault", "https://github.com/club/club-vault.git", null, "lead", false);

        var ex = await Assert.ThrowsAsync<PdmException>(() => new GitHubApi("token", new HttpClient(fake)).AcceptInvitationAsync(invitation));

        Assert.Contains("isn't there any more", ex.Message);
        Assert.Contains("ask lead to invite you again", ex.Message);
    }
}
