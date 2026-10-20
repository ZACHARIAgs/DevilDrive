using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Config;
using Jarvis.Core.Errors;

namespace Jarvis.Core.Git;

/// <summary>What the welcome screen found on this PC.</summary>
public sealed class ToolCheck
{
    public string? GitPath { get; set; }
    public string? GitVersion { get; set; }
    public string? LfsVersion { get; set; }
    public bool Ready => GitPath != null && LfsVersion != null;
}

/// <summary>
/// First-run checks: Git and Git LFS installed, and a GitHub sign-in saved in Git Credential Manager
/// (the same one Git uses for every vault). Signing in opens GCM's own GitHub window.
/// </summary>
public static class GitHubSignIn
{
    /// <summary>Nothing to open yet: no vault in the list exists on this PC.</summary>
    public static bool NeedsWelcome(UserSettings settings) =>
        !settings.Vaults.Any(v => Directory.Exists(Path.Combine(v, ".git")));

    public static async Task<ToolCheck> CheckToolsAsync(CancellationToken ct = default)
    {
        var check = new ToolCheck { GitPath = GitRunner.FindGit() };
        if (check.GitPath == null) return check;
        var git = new GitRunner(Path.GetTempPath(), check.GitPath);
        var version = await git.RunAsync(new[] { "--version" }, null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (version.Success) check.GitVersion = version.StdOut.Trim();
        var lfs = await git.RunAsync(new[] { "lfs", "version" }, null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (lfs.Success)
        {
            check.LfsVersion = lfs.StdOut.Trim();
            // Turns LFS on for this Windows account (what Setup does); harmless if it already is.
            await git.RunAsync(new[] { "lfs", "install", "--skip-repo" }, null, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        return check;
    }

    /// <summary>The account of the saved GitHub sign-in, or null if there's none or it expired. Throws when offline.</summary>
    public static async Task<GitHubUser?> GetSignedInUserAsync(CancellationToken ct = default)
    {
        var token = await GitHubCredentials.GetTokenAsync(ct).ConfigureAwait(false);
        if (token == null) return null;
        try
        {
            return await new GitHubApi(token).GetUserAsync(ct).ConfigureAwait(false);
        }
        catch (VaultOfflineException)
        {
            throw;
        }
        catch (PdmException)
        {
            return null; // expired or revoked
        }
    }

    /// <summary>
    /// Opens Git Credential Manager's GitHub sign-in (browser or device code), checks the account with
    /// GitHub and saves it for Git. A sign-in that GitHub no longer accepts is forgotten first.
    /// </summary>
    public static async Task<GitHubUser> SignInAsync(CancellationToken ct = default)
    {
        var gitExe = GitRunner.FindGit() ?? throw new GitNotFoundException();
        var git = new GitRunner(Path.GetTempPath(), gitExe);
        const string host = "protocol=https\nhost=github.com\n";

        var stale = await GitHubCredentials.GetTokenAsync(ct).ConfigureAwait(false);
        if (stale != null && await GetSignedInUserAsync(ct).ConfigureAwait(false) == null)
        {
            await git.RunAsync(new[] { "credential", "reject" }, host + $"username=x\npassword={stale}\n\n", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }

        // Without credential.interactive=never, GCM shows its sign-in window and waits for the user.
        var fill = await git.RunAsync(new[] { "credential", "fill" }, host + "\n", TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        string? Field(string key) => fill.StdOut.Split('\n').Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?.Substring(key.Length + 1);
        var username = Field("username");
        var password = Field("password");
        if (!fill.Success || string.IsNullOrEmpty(password))
        {
            throw new PdmException("GitHub sign-in didn't finish. Click \"Sign in to GitHub\" and complete the sign-in in the window or browser that opens. " +
                                   "If no window opens, reinstall Git for Windows with its default options (they include Git Credential Manager).",
                fill.StdErr);
        }

        GitHubUser user;
        try
        {
            user = await new GitHubApi(password!).GetUserAsync(ct).ConfigureAwait(false);
        }
        catch (VaultOfflineException)
        {
            throw;
        }
        catch (PdmException ex)
        {
            await git.RunAsync(new[] { "credential", "reject" }, host + $"username={username}\npassword={password}\n\n", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            throw new PdmException("GitHub didn't accept that sign-in. Click \"Sign in to GitHub\" to try again.", ex.Detail, ex);
        }

        await git.RunAsync(new[] { "credential", "approve" }, host + $"username={username ?? user.Login}\npassword={password}\n\n", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return user;
    }
}
