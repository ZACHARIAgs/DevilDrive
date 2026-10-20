using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CapstonePdm.Core.Config;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Git;

namespace CapstonePdm.Core.Vault;

/// <summary>A vault repo on GitHub that the signed-in user can access.</summary>
public sealed class RemoteVault
{
    /// <summary>owner/name</summary>
    public string FullName { get; set; } = "";
    public string CloneUrl { get; set; } = "";
    /// <summary>The vault's name from its pdm.json.</summary>
    public string VaultName { get; set; } = "";
    /// <summary>Folder every member keeps this vault in (pdm.json "localPath", else C:\Vaults\&lt;repo&gt;).</summary>
    public string LocalPath { get; set; } = "";
    public bool IsAdmin { get; set; }
    /// <summary>Set when this vault is already on this PC.</summary>
    public string? ExistingFolder { get; set; }
}

/// <summary>
/// Finds the vaults (GitHub repos with a pdm.json) the user can access and downloads them, so one
/// install of Jarvis can work with several groups' vaults.
/// </summary>
public static class VaultCatalog
{
    public const string DefaultRoot = @"C:\Vaults";
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Lists the user's vault repos on GitHub (owned, collaborator and organization repos).</summary>
    public static async Task<IReadOnlyList<RemoteVault>> ListAsync(CancellationToken ct = default)
    {
        var token = await GitHubCredentials.GetTokenAsync(ct).ConfigureAwait(false)
                    ?? throw new PdmException("You're not signed in to GitHub on this PC yet. Open any vault once (or run Setup) to sign in, then try again.");

        var repos = new List<(string FullName, string CloneUrl, bool Admin)>();
        for (var page = 1; page <= 10; page++)
        {
            using var json = await GetJsonAsync($"https://api.github.com/user/repos?per_page=100&page={page}&affiliation=owner,collaborator,organization_member&sort=updated", token, ct).ConfigureAwait(false)
                             ?? throw new PdmException("Couldn't get your repositories from GitHub. Check your internet connection and try again.");
            var items = json.RootElement.EnumerateArray().ToList();
            foreach (var repo in items)
            {
                var admin = repo.TryGetProperty("permissions", out var p) &&
                            ((p.TryGetProperty("admin", out var a) && a.GetBoolean()) || (p.TryGetProperty("maintain", out var m) && m.GetBoolean()));
                repos.Add((repo.GetProperty("full_name").GetString() ?? "", repo.GetProperty("clone_url").GetString() ?? "", admin));
            }
            if (items.Count < 100) break;
        }

        // A repo is a vault if it has a pdm.json at the top. Check several at a time.
        using var gate = new SemaphoreSlim(6);
        var checks = repos.Select(async repo =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var text = await GetRawAsync($"https://api.github.com/repos/{repo.FullName}/contents/{VaultConfig.FileName}", token, ct).ConfigureAwait(false);
                if (text == null) return null;
                VaultConfig config;
                try
                {
                    config = JsonSerializer.Deserialize<VaultConfig>(text, Json.Options) ?? new VaultConfig();
                }
                catch (JsonException)
                {
                    config = new VaultConfig();
                }
                var repoName = repo.FullName.Substring(repo.FullName.IndexOf('/') + 1);
                return new RemoteVault
                {
                    FullName = repo.FullName,
                    CloneUrl = repo.CloneUrl,
                    VaultName = string.IsNullOrWhiteSpace(config.VaultName) ? repoName : config.VaultName,
                    LocalPath = string.IsNullOrWhiteSpace(config.LocalPath) ? Path.Combine(DefaultRoot, repoName) : config.LocalPath!,
                    IsAdmin = repo.Admin,
                };
            }
            finally
            {
                gate.Release();
            }
        });
        var vaults = (await Task.WhenAll(checks).ConfigureAwait(false)).Where(v => v != null).Select(v => v!).ToList();
        return vaults.OrderBy(v => v.VaultName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The origin URL of a vault folder on this PC, or null.</summary>
    public static async Task<string?> RemoteUrlOfAsync(string folder, CancellationToken ct = default)
    {
        var gitExe = GitRunner.FindGit();
        if (gitExe == null || !Directory.Exists(Path.Combine(folder, ".git"))) return null;
        var result = await new GitRunner(folder, gitExe).RunAsync(new[] { "remote", "get-url", "origin" }, null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return result.Success ? result.StdOut.Trim() : null;
    }

    /// <summary>Same repo, ignoring ".git", case and a trailing slash.</summary>
    public static bool SameRepo(string? a, string? b)
    {
        static string Normalize(string url)
        {
            var u = url.Trim().TrimEnd('/').ToLowerInvariant();
            return u.EndsWith(".git", StringComparison.Ordinal) ? u.Substring(0, u.Length - 4) : u;
        }
        return a != null && b != null && Normalize(a) == Normalize(b);
    }

    /// <summary>
    /// Downloads a vault into <paramref name="folder"/> (must be missing or empty) with the same Git
    /// settings Setup uses: lock checks on, lockable files read-only, long paths.
    /// </summary>
    public static async Task CloneAsync(RemoteVault vault, string folder, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var gitExe = GitRunner.FindGit() ?? throw new PdmException("Git isn't installed on this PC. Run Setup first.");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new PdmException($"{folder} already exists and isn't empty. Choose another folder, or add it as an existing vault.");
        }
        var parent = Path.GetDirectoryName(Path.GetFullPath(folder))!;
        Directory.CreateDirectory(parent);

        progress?.Report($"Downloading {vault.VaultName} to {folder}. This can take a while for a big vault\u2026");
        var clone = await new GitRunner(parent, gitExe).RunAsync(new[] { "clone", vault.CloneUrl, folder }, null, TimeSpan.FromHours(2), ct).ConfigureAwait(false);
        if (!clone.Success)
        {
            throw GitErrorClassifier.Classify(clone, "download the vault");
        }

        var git = new GitRunner(folder, gitExe);
        foreach (var (key, value) in new[] { ("lfs.locksverify", "true"), ("lfs.setlockablereadonly", "true"), ("core.longpaths", "true") })
        {
            await git.RunAsync(new[] { "config", key, value }, null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        await VaultCreator.EnsureCommitIdentityAsync(git, async () =>
        {
            try
            {
                return await GitHubSignIn.GetSignedInUserAsync(ct).ConfigureAwait(false);
            }
            catch (PdmException)
            {
                return null; // offline: Setup's name is missing too; check-in explains
            }
        }, ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, string token, CancellationToken ct)
    {
        var text = await SendAsync(url, token, "application/vnd.github+json", ct).ConfigureAwait(false);
        return text == null ? null : JsonDocument.Parse(text);
    }

    private static Task<string?> GetRawAsync(string url, string token, CancellationToken ct) =>
        SendAsync(url, token, "application/vnd.github.raw+json", ct);

    private static async Task<string?> SendAsync(string url, string token, string accept, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("CapstonePDM");
        request.Headers.Accept.ParseAdd(accept);
        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
