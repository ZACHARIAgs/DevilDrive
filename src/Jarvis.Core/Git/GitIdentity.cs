using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Errors;

namespace Jarvis.Core.Git;

/// <summary>The name and email Git signs check-ins with (user.name / user.email).</summary>
public sealed class CommitIdentity
{
    public CommitIdentity(string? name, string? email)
    {
        Name = string.IsNullOrWhiteSpace(name) ? null : name!.Trim();
        Email = string.IsNullOrWhiteSpace(email) ? null : email!.Trim();
    }

    public string? Name { get; }
    public string? Email { get; }
    public bool IsComplete => Name != null && Email != null;
}

/// <summary>
/// Fills in Git's user.name and user.email from the GitHub account, so nobody has to type them:
/// the profile name (or username) and GitHub's private noreply address. Never changes values that are already set.
/// </summary>
public static class GitIdentity
{
    private static readonly string[] Global = { "--global" };

    /// <summary>What to suggest for this account: profile name (else username) and the noreply email.</summary>
    public static CommitIdentity Suggest(GitHubUser user) =>
        new CommitIdentity(string.IsNullOrWhiteSpace(user.Name) ? user.Login : user.Name, user.NoReplyEmail);

    /// <summary>The global user.name and user.email (either may be missing).</summary>
    public static Task<CommitIdentity> GetGlobalAsync(CancellationToken ct = default) => GetAsync(NewRunner(), Global, ct);

    /// <summary>Sets the global user.name and user.email, each only if it isn't set yet. Returns what Git uses now.</summary>
    public static Task<CommitIdentity> SetGlobalIfMissingAsync(string name, string email, CancellationToken ct = default) =>
        SetIfMissingAsync(NewRunner(), Global, name, email, ct);

    /// <summary>Checks a name and email typed on the welcome screen; returns what's wrong, or null.</summary>
    public static string? Validate(string? name, string? email)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Type your name (it's shown on every check-in).";
        if (string.IsNullOrWhiteSpace(email)) return "Type an email, or keep GitHub's private address.";
        var at = email!.Trim().IndexOf('@');
        if (at <= 0 || at == email.Trim().Length - 1 || email.Trim().Any(char.IsWhiteSpace)) return $"\"{email.Trim()}\" isn't an email address.";
        return null;
    }

    /// <param name="scope">"--global" for this Windows account; tests pass "--file", path.</param>
    internal static async Task<CommitIdentity> GetAsync(GitRunner git, IReadOnlyList<string> scope, CancellationToken ct)
    {
        async Task<string?> Read(string key)
        {
            var result = await git.RunAsync(new[] { "config" }.Concat(scope).Concat(new[] { key }).ToArray(), null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            return result.Success ? result.StdOut.Trim() : null; // exit code 1: not set
        }
        return new CommitIdentity(await Read("user.name").ConfigureAwait(false), await Read("user.email").ConfigureAwait(false));
    }

    internal static async Task<CommitIdentity> SetIfMissingAsync(GitRunner git, IReadOnlyList<string> scope, string name, string email, CancellationToken ct)
    {
        var current = await GetAsync(git, scope, ct).ConfigureAwait(false);
        async Task Write(string key, string value)
        {
            var result = await git.RunAsync(new[] { "config" }.Concat(scope).Concat(new[] { key, value.Trim() }).ToArray(), null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (!result.Success) throw new PdmException($"Git couldn't save your {(key == "user.name" ? "name" : "email")}: {result.StdErr.Trim()}", result.StdErr);
        }
        if (current.Name == null && !string.IsNullOrWhiteSpace(name)) await Write("user.name", name).ConfigureAwait(false);
        if (current.Email == null && !string.IsNullOrWhiteSpace(email)) await Write("user.email", email).ConfigureAwait(false);
        return await GetAsync(git, scope, ct).ConfigureAwait(false);
    }

    private static GitRunner NewRunner() =>
        new GitRunner(Path.GetTempPath(), GitRunner.FindGit() ?? throw new GitNotFoundException());
}
