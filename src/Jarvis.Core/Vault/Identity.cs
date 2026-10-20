using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Git;

namespace Jarvis.Core.Vault;

/// <summary>Who the user really is, as GitHub reports it (not a name they typed).</summary>
public sealed class VaultIdentity
{
    public VaultIdentity(string login, bool isRepoAdmin)
    {
        Login = login;
        IsRepoAdmin = isRepoAdmin;
    }

    /// <summary>GitHub username of the signed-in account.</summary>
    public string Login { get; }
    /// <summary>GitHub says this account is an admin (or maintainer) of the vault repo.</summary>
    public bool IsRepoAdmin { get; }
}

public interface IIdentityProvider
{
    /// <summary>Returns null when it can't be verified (offline, not signed in).</summary>
    Task<VaultIdentity?> GetIdentityAsync(IGitRunner git, string remoteUrl, CancellationToken ct);
}

/// <summary>
/// Asks GitHub who the saved sign-in belongs to. Uses the token Git Credential Manager already stores
/// for the vault (never prompts), calls GitHub's API with it, and keeps the token in memory only.
/// Typing someone else's username can't fool this: GitHub answers based on the token.
/// </summary>
public sealed class GitHubIdentityProvider : IIdentityProvider
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<VaultIdentity?> GetIdentityAsync(IGitRunner git, string remoteUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var remote) || remote.Scheme != Uri.UriSchemeHttps) return null;

        var fill = await git.RunAsync(new[] { "-c", "credential.interactive=never", "credential", "fill" },
            $"protocol=https\nhost={remote.Host}\n\n", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!fill.Success) return null;
        var token = fill.StdOut.Split('\n').Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith("password=", StringComparison.Ordinal))?.Substring("password=".Length);
        if (string.IsNullOrEmpty(token)) return null;

        var api = remote.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? "https://api.github.com" : $"https://{remote.Host}/api/v3";
        using var user = await GetJsonAsync($"{api}/user", token!, ct).ConfigureAwait(false);
        if (user == null || !user.RootElement.TryGetProperty("login", out var loginElement)) return null;
        var login = loginElement.GetString();
        if (string.IsNullOrEmpty(login)) return null;

        // owner/repo from https://github.com/owner/repo.git
        var isAdmin = false;
        var parts = remote.AbsolutePath.Trim('/').Split('/');
        if (parts.Length >= 2)
        {
            var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1].Substring(0, parts[1].Length - 4) : parts[1];
            using var repository = await GetJsonAsync($"{api}/repos/{parts[0]}/{repo}", token!, ct).ConfigureAwait(false);
            if (repository != null && repository.RootElement.TryGetProperty("permissions", out var permissions))
            {
                isAdmin = (permissions.TryGetProperty("admin", out var admin) && admin.GetBoolean()) ||
                          (permissions.TryGetProperty("maintain", out var maintain) && maintain.GetBoolean());
            }
        }
        return new VaultIdentity(login!, isAdmin);
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Jarvis");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timeout
        }
    }
}
