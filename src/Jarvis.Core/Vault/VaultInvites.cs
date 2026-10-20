using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Errors;
using Jarvis.Core.Git;

namespace Jarvis.Core.Vault;

/// <summary>Admins invite teammates to a vault: GitHub Write access plus a message telling them what to do.</summary>
public static class VaultInvites
{
    /// <summary>
    /// GitHub usernames from whatever was pasted: separated by spaces, commas, semicolons or lines,
    /// with or without "@" or a github.com/ link. Returns the valid ones (no duplicates) and the rest.
    /// </summary>
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) ParseUsernames(string? text)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (var raw in Regex.Split(text ?? "", @"[\s,;]+").Where(t => t.Length > 0))
        {
            var name = Regex.Replace(raw, @"^(?:https?://)?(?:www\.)?github\.com/", "", RegexOptions.IgnoreCase).TrimStart('@').TrimEnd('/');
            if (!VaultCreator.IsValidLogin(name)) invalid.Add(raw);
            else if (!valid.Contains(name, StringComparer.OrdinalIgnoreCase)) valid.Add(name);
        }
        return (valid, invalid);
    }

    /// <summary>owner and name from https://github.com/owner/name(.git).</summary>
    public static (string Owner, string Name)? ParseRepoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = Regex.Match(url!.Trim(), @"^https://github\.com/(?<o>[^/]+)/(?<n>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups["o"].Value, match.Groups["n"].Value) : null;
    }

    /// <summary>Invites each user to the repo with Write access, one at a time; never throws for one bad name.</summary>
    public static async Task<IReadOnlyList<InviteResult>> InviteAsync(GitHubApi api, string owner, string repo, IEnumerable<string> users, CancellationToken ct = default)
    {
        var results = new List<InviteResult>();
        foreach (var user in users)
        {
            if (string.Equals(user, owner, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new InviteResult(user, InviteOutcome.AlreadyMember, "Owns the vault repo."));
                continue;
            }
            results.Add(await api.InviteCollaboratorAsync(owner, repo, user, ct).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>The download page for the app, from the code repo this build updates from.</summary>
    public static string? InstallLink(string? updateRepo) =>
        string.IsNullOrWhiteSpace(updateRepo) ? null : $"https://github.com/{updateRepo}/releases/latest";

    /// <summary>What an admin sends new teammates (Discord, email, ...).</summary>
    public static string Message(string vaultName, string repoFullName, string localPath, string? installLink)
    {
        var install = installLink == null
            ? "Install Jarvis: ask me for the setup zip, unzip it and double-click Setup.cmd."
            : $"Install Jarvis: download Jarvis-Setup.zip from {installLink} , unzip it and double-click Setup.cmd. (Already have it? Skip this.)";
        return
            $"You're invited to our {vaultName}{(vaultName.EndsWith("vault", StringComparison.OrdinalIgnoreCase) ? "" : " vault")} (Jarvis, for our SolidWorks files).\n\n" +
            $"1. Accept the GitHub invite: https://github.com/{repoFullName}/invitations (it's also in your email). Sign in to GitHub first.\n" +
            $"2. {install}\n" +
            "3. Open Jarvis, click Vaults\u2026 then \"Find my vaults\", pick " + vaultName + " and click \"Download and open\".\n\n" +
            $"The vault goes to {localPath} on every PC so assemblies find their parts. Always open SolidWorks files from there, and check out before editing.";
    }

    /// <summary>Checks the signed-in user may invite people to the vault at <paramref name="remoteUrl"/>.</summary>
    public static async Task<(GitHubApi Api, GitHubRepo Repo)> ConnectAsAdminAsync(string? remoteUrl, CancellationToken ct = default)
    {
        var parsed = ParseRepoUrl(remoteUrl) ?? throw new PdmException("This vault isn't on github.com, so Jarvis can't invite people to it. Add them on the repo's page instead.");
        var api = await GitHubApi.SignedInAsync(ct).ConfigureAwait(false);
        var repo = await api.GetRepoAsync(parsed.Owner, parsed.Name, ct).ConfigureAwait(false)
                   ?? throw new PdmException($"GitHub didn't find {parsed.Owner}/{parsed.Name} for your account. Make sure you're signed in to GitHub with the account that's an admin of this vault.");
        if (!repo.CanInvite)
        {
            throw new PdmException($"Only people with the Admin role on {repo.FullName} can invite teammates (GitHub doesn't let the Maintain role do it). Ask the repo's owner to invite them.");
        }
        return (api, repo);
    }
}
