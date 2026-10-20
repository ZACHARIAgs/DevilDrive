using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Config;
using Jarvis.Core.Errors;
using Jarvis.Core.Git;

namespace Jarvis.Core.Vault;

/// <summary>What a team lead fills in to start a new vault.</summary>
public sealed class NewVaultRequest
{
    /// <summary>Shown in the app, e.g. "Formula SAE Vault".</summary>
    public string VaultName { get; set; } = "";
    /// <summary>GitHub account or organization that will own the repo.</summary>
    public string Owner { get; set; } = "";
    /// <summary>GitHub repo name, e.g. "formula-sae-vault".</summary>
    public string RepoName { get; set; } = "";
    /// <summary>SolidWorks version (year) everyone saves in; null for no check.</summary>
    public int? SolidWorksVersion { get; set; } = 2024;
    /// <summary>Folder every member keeps the vault in, e.g. C:\ClubVault.</summary>
    public string LocalPath { get; set; } = "";
}

public sealed class NewVaultResult
{
    public NewVaultResult(GitHubRepo repo, string folder, string vaultName)
    {
        Repo = repo;
        Folder = folder;
        VaultName = vaultName;
    }

    public GitHubRepo Repo { get; }
    public string Folder { get; }
    public string VaultName { get; }
}

/// <summary>
/// Creates a team vault from the app, what scripts/Initialize-Vault.ps1 used to do by hand: a new
/// private GitHub repo with the vault rules (.gitattributes, .gitignore), team settings (pdm.json)
/// and the vault guard, downloaded to the folder every member will use.
/// </summary>
public static class VaultCreator
{
    public const int DefaultSolidWorksVersion = 2024;

    private static readonly Regex GitHubLogin = new Regex(@"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$", RegexOptions.Compiled);
    private static readonly Regex RepoNameChars = new Regex(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled);

    /// <summary>"Formula SAE Vault" -> "formula-sae-vault".</summary>
    public static string RepoNameFor(string vaultName)
    {
        var slug = Regex.Replace((vaultName ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9._]+", "-").Trim('-', '.');
        if (slug.Length > 100) slug = slug.Substring(0, 100).Trim('-', '.');
        return slug.Length == 0 ? "vault" : slug;
    }

    public static bool IsValidLogin(string? login) => login != null && GitHubLogin.IsMatch(login);

    /// <summary>The first problem with the request, in words the user can act on, or null if it's fine.</summary>
    public static string? Validate(NewVaultRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VaultName)) return "Enter a name for the vault.";
        if (!IsValidLogin(request.Owner?.Trim())) return "Enter the GitHub account or organization that will own the vault (letters, numbers and dashes).";
        var repo = request.RepoName?.Trim() ?? "";
        if (!RepoNameChars.IsMatch(repo) || repo == "." || repo == "..") return "The repo name can only use letters, numbers, dashes, dots and underscores (up to 100).";
        if (request.SolidWorksVersion is { } year && (year < 2015 || year > 2100)) return "Enter the SolidWorks version as a year, e.g. 2024.";
        var path = (request.LocalPath?.Trim() ?? "").Replace('/', '\\');
        if (!Regex.IsMatch(path, @"^[A-Za-z]:\\[^<>:""/|?*]+$")) return @"Enter a full folder path on this PC, e.g. C:\ClubVault.";
        if (Path.GetPathRoot(path)?.TrimEnd('\\') is { } root && string.Equals(path.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)) return "Choose a folder, not a whole drive.";
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) return $"{path} already exists and isn't empty. Choose a new folder.";
        return null;
    }

    /// <summary>pdm.json from the template with this vault's name, folder and SolidWorks version (comments kept).</summary>
    public static string FillConfig(string template, string vaultName, string localPath, int? solidWorksVersion)
    {
        string Replace(string text, string key, string valuePattern, string value) =>
            Regex.Replace(text, "\"" + key + "\"\\s*:\\s*" + valuePattern, m => "\"" + key + "\": " + value);

        var text = Replace(template, "vaultName", "\"(?:[^\"\\\\]|\\\\.)*\"", JsonSerializer.Serialize(vaultName.Trim()));
        text = Replace(text, "localPath", "(?:\"(?:[^\"\\\\]|\\\\.)*\"|null)", JsonSerializer.Serialize(localPath.Trim()));
        text = Replace(text, "solidWorksVersion", "(?:\\d+|null)", solidWorksVersion?.ToString() ?? "null");
        return text;
    }

    /// <summary>Every file of a new vault, by path relative to the vault folder.</summary>
    public static IReadOnlyDictionary<string, string> TemplateFiles(string vaultName, string localPath, int? solidWorksVersion) =>
        new Dictionary<string, string>
        {
            [VaultSetup.AttributesPath] = VaultSetup.TemplateAttributes,
            [".gitignore"] = VaultSetup.TemplateIgnore,
            [VaultConfig.FileName] = FillConfig(VaultSetup.TemplateConfig, vaultName, localPath, solidWorksVersion),
            [VaultSetup.GuardWorkflowPath] = VaultSetup.GuardWorkflow,
            [VaultSetup.GuardScriptPath] = VaultSetup.GuardScript,
        };

    /// <summary>
    /// Creates the private repo, puts the vault files in it and sets up <see cref="NewVaultRequest.LocalPath"/>
    /// like a teammate's download. If an earlier try already created the repo but couldn't upload, the
    /// empty repo is reused. Nothing is ever deleted on GitHub.
    /// </summary>
    public static async Task<NewVaultResult> CreateAsync(NewVaultRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var problem = Validate(request);
        if (problem != null) throw new PdmException(problem);
        var owner = request.Owner.Trim();
        var name = request.RepoName.Trim();
        var folder = Path.GetFullPath(request.LocalPath.Trim()).TrimEnd('\\');

        var gitExe = GitRunner.FindGit() ?? throw new GitNotFoundException();
        var tools = new GitRunner(Path.GetTempPath(), gitExe);
        var lfs = await tools.RunAsync(new[] { "lfs", "install", "--skip-repo" }, null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!lfs.Success) throw new PdmException("Git LFS isn't installed on this PC. Install it (the welcome screen or Setup does this), then try again.", lfs.StdErr);

        progress?.Report("Checking your GitHub sign-in\u2026");
        var api = await GitHubApi.SignedInAsync(ct).ConfigureAwait(false);
        var user = await api.GetUserAsync(ct).ConfigureAwait(false);
        var ownerIsUser = string.Equals(owner, user.Login, StringComparison.OrdinalIgnoreCase);

        progress?.Report($"Creating the private repo {owner}/{name} on GitHub\u2026");
        GitHubRepo repo;
        try
        {
            repo = await api.CreatePrivateRepoAsync(owner, ownerIsUser, name, $"Jarvis vault: {request.VaultName.Trim()}", ct).ConfigureAwait(false);
        }
        catch (RepoNameTakenException)
        {
            // Reuse it only if it's an empty repo this user administers (e.g. an earlier try that couldn't upload).
            var existing = await api.GetRepoAsync(owner, name, ct).ConfigureAwait(false);
            if (existing == null || !existing.IsAdmin || !await IsEmptyAsync(tools, existing.CloneUrl, ct).ConfigureAwait(false)) throw;
            progress?.Report($"Using the empty repo {existing.FullName} that's already on GitHub\u2026");
            repo = existing;
        }

        var createdFolder = !Directory.Exists(folder);
        try
        {
            var files = TemplateFiles(request.VaultName, folder, request.SolidWorksVersion);
            await SeedAsync(gitExe, folder, repo.CloneUrl, files, user, configureLfs: true, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PdmException || ex is IOException || ex is UnauthorizedAccessException)
        {
            TryDelete(folder, createdFolder);
            var detail = ex is PdmException pdm ? pdm.Detail : ex.ToString();
            throw new PdmException($"The repo {repo.FullName} was created on GitHub, but the vault files couldn't be uploaded: {ex.Message}\n\n" +
                                   "Fix that and click Create again: Jarvis reuses the empty repo.", detail, ex);
        }

        progress?.Report("Vault ready.");
        return new NewVaultResult(repo, folder, request.VaultName.Trim());
    }

    /// <summary>
    /// Makes <paramref name="folder"/> a vault: Git with the same settings a download gets, the vault
    /// files committed and pushed to <paramref name="remoteUrl"/>'s main branch.
    /// </summary>
    internal static async Task SeedAsync(string gitExe, string folder, string remoteUrl, IReadOnlyDictionary<string, string> files, GitHubUser? user,
        bool configureLfs, IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var git = new GitRunner(folder, gitExe);

        async Task Run(string action, params string[] args)
        {
            var result = await git.RunAsync(args, null, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            if (!result.Success) throw GitErrorClassifier.Classify(result, action);
        }

        progress?.Report($"Setting up {folder}\u2026");
        await Run("set up the vault folder", "init").ConfigureAwait(false);
        await Run("set up the vault folder", "symbolic-ref", "HEAD", "refs/heads/main").ConfigureAwait(false);
        var settings = new List<(string, string)> { ("core.longpaths", "true") };
        if (configureLfs) settings.AddRange(new[] { ("lfs.locksverify", "true"), ("lfs.setlockablereadonly", "true") });
        foreach (var (key, value) in settings) await Run("set up the vault folder", "config", key, value).ConfigureAwait(false);
        if (configureLfs) await Run("set up Git LFS in the vault", "lfs", "update", "--force").ConfigureAwait(false);

        await EnsureCommitIdentityAsync(git, () => Task.FromResult(user), ct).ConfigureAwait(false);

        foreach (var file in files)
        {
            var path = Path.Combine(folder, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value, new UTF8Encoding(false));
        }

        await Run("set up the vault folder", "remote", "add", "origin", remoteUrl).ConfigureAwait(false);
        await Run("add the vault files", new[] { "add", "--" }.Concat(files.Keys).ToArray()).ConfigureAwait(false);
        var message = Path.Combine(Path.GetTempPath(), "capstone-pdm-commit-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(message, "Create the vault (Jarvis rules, vault guard, team settings)\n", new UTF8Encoding(false));
            await Run("add the vault files", "commit", "-F", message).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(message);
        }

        // A brand-new GitHub repo can take a few seconds before it accepts pushes.
        progress?.Report("Uploading the vault files to GitHub\u2026");
        GitResult push = null!;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            push = await git.RunAsync(new[] { "push", "-u", "origin", "main" }, null, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            if (push.Success) return;
            if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct).ConfigureAwait(false);
        }
        throw GitErrorClassifier.Classify(push, "upload the vault files");
    }

    /// <summary>
    /// Check-ins need a name and email; Setup normally sets them for the Windows account. If they're
    /// missing (the app was installed without Setup), use the GitHub account's for this vault.
    /// </summary>
    internal static async Task EnsureCommitIdentityAsync(GitRunner git, Func<Task<GitHubUser?>> getUser, CancellationToken ct)
    {
        var name = await git.RunAsync(new[] { "config", "user.name" }, null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var email = await git.RunAsync(new[] { "config", "user.email" }, null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var needName = !name.Success || string.IsNullOrWhiteSpace(name.StdOut);
        var needEmail = !email.Success || string.IsNullOrWhiteSpace(email.StdOut);
        if (!needName && !needEmail) return;
        var user = await getUser().ConfigureAwait(false);
        if (user == null) return;
        if (needName) await git.RunAsync(new[] { "config", "user.name", string.IsNullOrWhiteSpace(user.Name) ? user.Login : user.Name! }, null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (needEmail) await git.RunAsync(new[] { "config", "user.email", user.NoReplyEmail }, null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    /// <summary>The repo has no branches yet (nothing was ever pushed).</summary>
    private static async Task<bool> IsEmptyAsync(GitRunner git, string cloneUrl, CancellationToken ct)
    {
        var refs = await git.RunAsync(new[] { "ls-remote", "--heads", cloneUrl }, null, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        return refs.Success && string.IsNullOrWhiteSpace(refs.StdOut);
    }

    /// <summary>Removes the half-made vault folder, but only if this run created it.</summary>
    private static void TryDelete(string folder, bool createdFolder)
    {
        if (!createdFolder || !Directory.Exists(folder)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Left behind; the next try asks for an empty folder.
        }
    }
}
