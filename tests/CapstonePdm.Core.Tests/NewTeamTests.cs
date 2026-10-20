using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CapstonePdm.Core.Config;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Git;
using CapstonePdm.Core.Vault;
using Xunit;

namespace CapstonePdm.Core.Tests;

/// <summary>New team vault, inviting teammates and the first-run checks.</summary>
public class NewTeamTests
{
    // ------------------------------------------------------------ new vault

    [Theory]
    [InlineData("Formula SAE Vault", "formula-sae-vault")]
    [InlineData("  Robotics Club!! 2026 ", "robotics-club-2026")]
    [InlineData("team_42.cad", "team_42.cad")]
    [InlineData("\u00C9quipe", "quipe")]
    [InlineData("!!!", "vault")]
    public void RepoNameFor_MakesAGitHubFriendlyName(string vaultName, string repo) =>
        Assert.Equal(repo, VaultCreator.RepoNameFor(vaultName));

    private static NewVaultRequest Request(Action<NewVaultRequest>? change = null)
    {
        var request = new NewVaultRequest
        {
            VaultName = "Club Vault",
            Owner = "robotics-club",
            RepoName = "club-vault",
            SolidWorksVersion = 2024,
            LocalPath = Path.Combine(Path.GetTempPath(), "capstone-pdm-tests", Guid.NewGuid().ToString("N")),
        };
        change?.Invoke(request);
        return request;
    }

    [Fact]
    public void Validate_AcceptsAGoodRequest() => Assert.Null(VaultCreator.Validate(Request()));

    [Theory]
    [InlineData("name", "")]
    [InlineData("owner", "-bad-")]
    [InlineData("owner", "two words")]
    [InlineData("repo", "has space")]
    [InlineData("repo", "..")]
    [InlineData("folder", "ClubVault")]
    [InlineData("folder", "C:/")]
    [InlineData("folder", @"C:\")]
    [InlineData("folder", @"\\server\share\vault")]
    [InlineData("version", "1999")]
    public void Validate_ExplainsWhatsWrong(string field, string value)
    {
        var request = Request(r =>
        {
            switch (field)
            {
                case "name": r.VaultName = value; break;
                case "owner": r.Owner = value; break;
                case "repo": r.RepoName = value; break;
                case "folder": r.LocalPath = value; break;
                case "version": r.SolidWorksVersion = int.Parse(value); break;
            }
        });
        Assert.NotNull(VaultCreator.Validate(request));
    }

    [Fact]
    public void Validate_RefusesAFolderThatIsntEmpty()
    {
        var folder = Path.Combine(Path.GetTempPath(), "capstone-pdm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "old.txt"), "x");
            Assert.Contains("isn't empty", VaultCreator.Validate(Request(r => r.LocalPath = folder)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void FillConfig_SetsNameFolderAndVersion_AndKeepsComments()
    {
        var text = VaultCreator.FillConfig(VaultSetup.TemplateConfig, "Club \"Bots\" Vault", @"C:\ClubVault", 2022);
        var config = System.Text.Json.JsonSerializer.Deserialize<VaultConfig>(text, Json.Options)!;

        Assert.Equal("Club \"Bots\" Vault", config.VaultName);
        Assert.Equal(@"C:\ClubVault", config.LocalPath);
        Assert.Equal(2022, config.SolidWorksVersion);
        Assert.Equal("main", config.Branch);
        Assert.Contains("// Discord", text);
    }

    [Fact]
    public void FillConfig_NoVersionMeansNoCheck()
    {
        var text = VaultCreator.FillConfig(VaultSetup.TemplateConfig, "V", @"C:\V", null);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<VaultConfig>(text, Json.Options)!.SolidWorksVersion);
    }

    [Fact]
    public void TemplateFiles_HaveTheRulesSettingsAndGuard()
    {
        var files = VaultCreator.TemplateFiles("Club Vault", @"C:\ClubVault", 2024);

        Assert.Equal(new[] { ".gitattributes", ".github/vault-guard/guard.py", ".github/workflows/vault-guard.yml", ".gitignore", "pdm.json" },
            files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains("*.sldprt filter=lfs diff=lfs merge=lfs -text lockable", files[".gitattributes"]);
        Assert.Contains(VaultSetup.PreviewAttributesRule, files[".gitattributes"]);
        Assert.Equal(VaultSetup.GuardScript, files[".github/vault-guard/guard.py"]);
        Assert.Contains("~$*", files[".gitignore"]);
    }

    // ------------------------------------------------------------ invites

    [Fact]
    public void ParseUsernames_AcceptsPastedLists()
    {
        var (valid, invalid) = VaultInvites.ParseUsernames("alice, @bob\nhttps://github.com/carol-x/ ;alice  not_valid -dash");

        Assert.Equal(new[] { "alice", "bob", "carol-x" }, valid);
        Assert.Equal(new[] { "not_valid", "-dash" }, invalid);
    }

    [Theory]
    [InlineData("https://github.com/team/club-vault.git", "team", "club-vault")]
    [InlineData("https://github.com/team/club-vault", "team", "club-vault")]
    public void ParseRepoUrl_ReadsOwnerAndName(string url, string owner, string name) =>
        Assert.Equal((owner, name), VaultInvites.ParseRepoUrl(url));

    [Theory]
    [InlineData("git@github.com:team/vault.git")]
    [InlineData("https://gitlab.com/team/vault.git")]
    [InlineData(null)]
    public void ParseRepoUrl_OnlyGitHubHttps(string? url) => Assert.Null(VaultInvites.ParseRepoUrl(url));

    [Fact]
    public void Message_SaysWhatToDoNext()
    {
        var link = VaultInvites.InstallLink("team/capstone-pdm");
        var message = VaultInvites.Message("Club Vault", "team/club-vault", @"C:\ClubVault", link);

        Assert.Equal("https://github.com/team/capstone-pdm/releases/latest", link);
        Assert.Contains("https://github.com/team/club-vault/invitations", message);
        Assert.Contains(link!, message);
        Assert.Contains("Find my vaults", message);
        Assert.Contains(@"C:\ClubVault", message);
        Assert.StartsWith("You're invited to our Club Vault (", message);
        Assert.StartsWith("You're invited to our Robotics vault (", VaultInvites.Message("Robotics", "t/v", @"C:\V", null));
        Assert.Contains("setup zip", VaultInvites.Message("V", "t/v", @"C:\V", null));
    }

    // ------------------------------------------------------------ GitHub API (fake GitHub)

    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;

        public FakeGitHub(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            return _respond(request, body);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode code, string json = "") =>
        new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string RepoJson =
        "{\"full_name\":\"club/club-vault\",\"clone_url\":\"https://github.com/club/club-vault.git\",\"html_url\":\"https://github.com/club/club-vault\"," +
        "\"private\":true,\"size\":0,\"permissions\":{\"admin\":true,\"maintain\":true,\"push\":true}}";

    [Fact]
    public async Task CreateRepo_InAnOrganization_IsPrivate()
    {
        var fake = new FakeGitHub((_, _) => Reply(HttpStatusCode.Created, RepoJson));
        var repo = await new GitHubApi("token", new HttpClient(fake)).CreatePrivateRepoAsync("club", false, "club-vault", "Club");

        Assert.Equal("club/club-vault", repo.FullName);
        Assert.True(repo.IsPrivate && repo.IsAdmin && repo.CanInvite);
        var request = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/orgs/club/repos", request.Path);
        Assert.Contains("\"private\":true", request.Body);
        Assert.Contains("\"name\":\"club-vault\"", request.Body);
    }

    [Fact]
    public async Task CreateRepo_OnTheUsersAccount()
    {
        var fake = new FakeGitHub((_, _) => Reply(HttpStatusCode.Created, RepoJson));
        await new GitHubApi("token", new HttpClient(fake)).CreatePrivateRepoAsync("sam", true, "club-vault", "Club");
        Assert.Equal("/user/repos", fake.Requests.Single().Path);
    }

    [Fact]
    public async Task CreateRepo_NameTaken()
    {
        var fake = new FakeGitHub((_, _) => Reply((HttpStatusCode)422,
            "{\"message\":\"Repository creation failed.\",\"errors\":[{\"resource\":\"Repository\",\"code\":\"custom\",\"field\":\"name\",\"message\":\"name already exists on this account\"}]}"));
        var ex = await Assert.ThrowsAsync<RepoNameTakenException>(() =>
            new GitHubApi("token", new HttpClient(fake)).CreatePrivateRepoAsync("club", false, "club-vault", "Club"));
        Assert.Contains("already has a repo named \"club-vault\"", ex.Message);
    }

    [Fact]
    public async Task CreateRepo_NoPermissionInOrganization()
    {
        var fake = new FakeGitHub((_, _) => Reply(HttpStatusCode.Forbidden, "{\"message\":\"You need admin access to the organization before adding a repository to it.\"}"));
        var ex = await Assert.ThrowsAsync<PdmException>(() =>
            new GitHubApi("token", new HttpClient(fake)).CreatePrivateRepoAsync("club", false, "club-vault", "Club"));
        Assert.Contains("can't create repos in the club organization", ex.Message);
    }

    [Fact]
    public async Task CreateRepo_Offline()
    {
        var fake = new FakeGitHub((_, _) => throw new HttpRequestException("No such host is known."));
        await Assert.ThrowsAsync<VaultOfflineException>(() =>
            new GitHubApi("token", new HttpClient(fake)).CreatePrivateRepoAsync("club", false, "club-vault", "Club"));
    }

    [Fact]
    public async Task Invite_ReportsEachTeammate()
    {
        var fake = new FakeGitHub((request, _) => request.RequestUri!.AbsolutePath.Split('/').Last() switch
        {
            "alice" => Reply(HttpStatusCode.Created, "{\"id\":1}"),
            "bob" => Reply(HttpStatusCode.NoContent),
            "ghost" => Reply(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"),
            _ => Reply((HttpStatusCode)422, "{\"message\":\"Validation Failed\",\"errors\":[{\"message\":\"Collaborator limit reached\"}]}"),
        });
        var api = new GitHubApi("token", new HttpClient(fake));

        var results = await VaultInvites.InviteAsync(api, "club", "club-vault", new[] { "alice", "bob", "ghost", "dave", "CLUB" });

        Assert.Equal(new[] { InviteOutcome.Invited, InviteOutcome.AlreadyMember, InviteOutcome.NotFound, InviteOutcome.Failed, InviteOutcome.AlreadyMember },
            results.Select(r => r.Outcome));
        Assert.Contains("Collaborator limit reached", results[3].Message);
        Assert.Equal(4, fake.Requests.Count); // never invites the owner
        Assert.All(fake.Requests, r =>
        {
            Assert.Equal(HttpMethod.Put, r.Method);
            Assert.StartsWith("/repos/club/club-vault/collaborators/", r.Path);
            Assert.Contains("\"permission\":\"push\"", r.Body);
        });
    }

    [Fact]
    public async Task Invite_OfflineIsAFailureNotACrash()
    {
        var fake = new FakeGitHub((_, _) => throw new HttpRequestException("offline"));
        var result = await new GitHubApi("token", new HttpClient(fake)).InviteCollaboratorAsync("club", "club-vault", "alice");
        Assert.Equal(InviteOutcome.Failed, result.Outcome);
    }

    // ------------------------------------------------------------ first run

    [Fact]
    public void Welcome_OnlyWhenNoVaultIsOnThisPc()
    {
        var root = Path.Combine(Path.GetTempPath(), "capstone-pdm-tests", Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, ".git"));
        try
        {
            var none = new UserSettings { VaultPath = Path.Combine(root, "missing") };
            none.Normalize();
            Assert.True(GitHubSignIn.NeedsWelcome(none));

            var some = new UserSettings { VaultPath = Path.Combine(root, "missing"), Vaults = new List<string> { vault } };
            some.Normalize();
            Assert.False(GitHubSignIn.NeedsWelcome(some));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

/// <summary>Seeds a new vault into a local bare repo (standing in for the new GitHub repo) and downloads it like a teammate.</summary>
public sealed class NewVaultIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "capstone-pdm-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Seed_PushesTheVaultFilesToMain()
    {
        var remote = Path.Combine(_root, "remote.git");
        var folder = Path.Combine(_root, "ClubVault");
        Directory.CreateDirectory(_root);
        Git(_root, "init", "--bare", remote);
        var gitExe = GitRunner.FindGit()!;
        var files = VaultCreator.TemplateFiles("Club Vault", folder, 2024);

        await VaultCreator.SeedAsync(gitExe, folder, remote, files, new GitHubUser("sam", 42, "Sam Lee"), configureLfs: false, null, CancellationToken.None);

        Assert.Equal("main", Git(folder, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("origin/main", Git(folder, "rev-parse", "--abbrev-ref", "main@{upstream}"));
        Assert.Equal("true", Git(folder, "config", "core.longpaths"));

        var teammate = Path.Combine(_root, "teammate");
        Git(_root, "clone", "--branch", "main", remote, teammate);
        foreach (var path in files.Keys) Assert.True(File.Exists(Path.Combine(teammate, path)), path);
        var config = VaultConfig.Load(teammate);
        Assert.Equal("Club Vault", config.VaultName);
        Assert.Equal(folder, config.LocalPath);
        Assert.Equal(2024, config.SolidWorksVersion);
        Assert.Equal("Create the vault (Jarvis rules, vault guard, team settings)", Git(teammate, "log", "-1", "--format=%s"));
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Temp folder; Windows cleans it up eventually.
        }
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {error}");
        return output.Trim();
    }
}
