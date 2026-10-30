using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Errors;

namespace Jarvis.Core.Git;

/// <summary>The signed-in GitHub account.</summary>
public sealed class GitHubUser
{
    public GitHubUser(string login, long id, string? name)
    {
        Login = login;
        Id = id;
        Name = name;
    }

    public string Login { get; }
    public long Id { get; }
    public string? Name { get; }

    /// <summary>GitHub's private commit email for this account.</summary>
    public string NoReplyEmail => $"{Id}+{Login}@users.noreply.github.com";
}

/// <summary>A repo as GitHub reports it to the signed-in user.</summary>
public sealed class GitHubRepo
{
    /// <summary>owner/name</summary>
    public string FullName { get; set; } = "";
    public string CloneUrl { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public bool IsPrivate { get; set; }
    /// <summary>Size in KB; 0 for a repo nothing was pushed to yet.</summary>
    public long Size { get; set; }
    public bool CanPush { get; set; }
    /// <summary>Admin or Maintain role: may change settings and invite people.</summary>
    public bool IsAdmin { get; set; }
    /// <summary>Admin role (not just Maintain): GitHub only lets admins invite people.</summary>
    public bool CanInvite { get; set; }
}

/// <summary>Someone with access to a repo.</summary>
public sealed class GitHubCollaborator
{
    public GitHubCollaborator(string login, string role)
    {
        Login = login;
        Role = role;
    }

    public string Login { get; }
    /// <summary>GitHub's role name: admin, maintain, write, triage or read.</summary>
    public string Role { get; }
}

/// <summary>An invitation to a repo that hasn't been accepted yet.</summary>
public sealed class GitHubInvitation
{
    public GitHubInvitation(long id, string login, DateTimeOffset? createdAt, bool expired)
    {
        Id = id;
        Login = login;
        CreatedAt = createdAt;
        Expired = expired;
    }

    public long Id { get; }
    public string Login { get; }
    public DateTimeOffset? CreatedAt { get; }
    /// <summary>GitHub invitations expire after 7 days; invite again to send a new one.</summary>
    public bool Expired { get; }
}

/// <summary>An invitation the signed-in user received to someone else's repo.</summary>
public sealed class GitHubRepoInvitation
{
    public GitHubRepoInvitation(long id, string repoFullName, string cloneUrl, string? description, string inviter, bool expired)
    {
        Id = id;
        RepoFullName = repoFullName;
        CloneUrl = cloneUrl;
        Description = description;
        Inviter = inviter;
        Expired = expired;
    }

    public long Id { get; }
    /// <summary>owner/name</summary>
    public string RepoFullName { get; }
    public string CloneUrl { get; }
    public string? Description { get; }
    /// <summary>GitHub username of whoever sent it.</summary>
    public string Inviter { get; }
    public bool Expired { get; }

    /// <summary>
    /// Made by "New team vault" (its description says so) or named like one. A repo's pdm.json can't
    /// be read before the invitation is accepted, so this is the best guess until then.
    /// </summary>
    public bool LooksLikeVault =>
        (Description ?? "").IndexOf("DevilDrive vault", StringComparison.OrdinalIgnoreCase) >= 0 ||
        (Description ?? "").IndexOf("Jarvis vault", StringComparison.OrdinalIgnoreCase) >= 0 ||
        RepoFullName.IndexOf("vault", StringComparison.OrdinalIgnoreCase) >= 0 ||
        (Description ?? "").IndexOf("vault", StringComparison.OrdinalIgnoreCase) >= 0;
}

public enum InviteOutcome
{
    /// <summary>GitHub sent them an invitation to accept.</summary>
    Invited,
    /// <summary>They already have access (or already have a pending invite that was updated).</summary>
    AlreadyMember,
    /// <summary>No GitHub account with that username.</summary>
    NotFound,
    Failed,
}

public sealed class InviteResult
{
    public InviteResult(string user, InviteOutcome outcome, string message)
    {
        User = user;
        Outcome = outcome;
        Message = message;
    }

    public string User { get; }
    public InviteOutcome Outcome { get; }
    public string Message { get; }
    public bool Succeeded => Outcome == InviteOutcome.Invited || Outcome == InviteOutcome.AlreadyMember;
}

/// <summary>
/// The few GitHub REST calls Jarvis makes as the user: who am I, create a private vault repo,
/// invite collaborators. Uses the token Git Credential Manager saved, kept in memory only.
/// </summary>
public sealed class GitHubApi
{
    public const string ApiRoot = "https://api.github.com";
    private static readonly HttpClient SharedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _http;
    private readonly string _token;

    public GitHubApi(string token, HttpClient? http = null)
    {
        _token = token;
        _http = http ?? SharedHttp;
    }

    /// <summary>Uses the GitHub sign-in saved on this PC, or explains how to sign in.</summary>
    public static async Task<GitHubApi> SignedInAsync(CancellationToken ct = default)
    {
        var token = await GitHubCredentials.GetTokenAsync(ct).ConfigureAwait(false)
                    ?? throw new PdmException("You're not signed in to GitHub on this PC yet. Use \"Sign in to GitHub\" on the welcome screen (or open any vault once), then try again.");
        return new GitHubApi(token);
    }

    public async Task<GitHubUser> GetUserAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/user", null, "check your GitHub sign-in", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new PdmException("Your saved GitHub sign-in has expired. Sign in to GitHub again, then try again.");
        }
        using var json = await ReadJsonAsync(response, "check your GitHub sign-in").ConfigureAwait(false);
        var root = json.RootElement;
        return new GitHubUser(root.GetProperty("login").GetString() ?? "", root.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
            root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null);
    }

    /// <summary>Organizations the user belongs to (they can create a vault there if the organization allows it).</summary>
    public async Task<IReadOnlyList<string>> GetOrganizationsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "/user/orgs?per_page=100", null, "list your organizations", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<string>();
            using var json = await ReadJsonAsync(response, "list your organizations").ConfigureAwait(false);
            return json.RootElement.EnumerateArray().Select(o => o.GetProperty("login").GetString() ?? "").Where(o => o.Length > 0).ToList();
        }
        catch (PdmException)
        {
            return Array.Empty<string>(); // only used to suggest owners
        }
    }

    /// <summary>The repo, or null if it doesn't exist (or the user can't see it).</summary>
    public async Task<GitHubRepo?> GetRepoAsync(string owner, string name, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/repos/{owner}/{name}", null, "look up the vault repo", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var json = await ReadJsonAsync(response, "look up the vault repo").ConfigureAwait(false);
        return ParseRepo(json.RootElement);
    }

    /// <summary>
    /// Creates an empty private repo under the user's account (<paramref name="owner"/> is their login)
    /// or an organization. Throws a <see cref="PdmException"/> that says what to do when the name is
    /// taken or the user may not create repos there.
    /// </summary>
    public async Task<GitHubRepo> CreatePrivateRepoAsync(string owner, bool ownerIsUser, string name, string description, CancellationToken ct = default)
    {
        var path = ownerIsUser ? "/user/repos" : $"/orgs/{owner}/repos";
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = name,
            ["description"] = description,
            ["private"] = true,
            ["auto_init"] = false,
            ["has_wiki"] = false,
        });
        const string action = "create the vault repo";
        using var response = await SendAsync(HttpMethod.Post, path, body, action, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        switch ((int)response.StatusCode)
        {
            case 201:
                using (var json = JsonDocument.Parse(text)) return ParseRepo(json.RootElement);
            case 422 when text.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0:
                throw new RepoNameTakenException(owner, name);
            case 422:
                throw new PdmException($"GitHub didn't accept the repo name \"{name}\": {ErrorMessageOf(text)}. Try a shorter name with only letters, numbers and dashes.", text);
            case 401:
                throw new PdmException("Your saved GitHub sign-in has expired. Sign in to GitHub again, then try again.", text);
            case 403:
            case 404:
                throw new PdmException(ownerIsUser
                        ? "GitHub didn't let Jarvis create a repo on your account. Sign in to GitHub again (the sign-in needs the \"repo\" permission), then try again."
                        : $"You can't create repos in the {owner} organization. Ask one of its owners to make you an owner, or to allow members to create private repos (organization Settings, Member privileges), or create the vault on your own account.",
                    text);
            default:
                throw new PdmException($"GitHub couldn't create the repo ({(int)response.StatusCode}: {ErrorMessageOf(text)}). Try again in a minute.", text);
        }
    }

    /// <summary>Invites a GitHub user to the repo with Write access (can check out and check in, can't change settings).</summary>
    public async Task<InviteResult> InviteCollaboratorAsync(string owner, string repo, string user, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["permission"] = "push" });
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(HttpMethod.Put, $"/repos/{owner}/{repo}/collaborators/{Uri.EscapeDataString(user)}", body, "invite teammates", ct).ConfigureAwait(false);
        }
        catch (VaultOfflineException ex)
        {
            return new InviteResult(user, InviteOutcome.Failed, ex.Message);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (int)response.StatusCode switch
            {
                201 => new InviteResult(user, InviteOutcome.Invited, "Invitation sent. They need to accept it on GitHub (it's also in their email)."),
                204 => new InviteResult(user, InviteOutcome.AlreadyMember, "Already has access to the vault."),
                404 => new InviteResult(user, InviteOutcome.NotFound, "There's no GitHub account with this username (or you aren't an admin of the vault repo)."),
                403 => new InviteResult(user, InviteOutcome.Failed, "GitHub refused: only the vault's admins can invite people. " + ErrorMessageOf(text)),
                422 => new InviteResult(user, InviteOutcome.Failed, ErrorMessageOf(text)),
                401 => new InviteResult(user, InviteOutcome.Failed, "Your saved GitHub sign-in has expired. Sign in to GitHub again."),
                var code => new InviteResult(user, InviteOutcome.Failed, $"GitHub error {code}: {ErrorMessageOf(text)}"),
            };
        }
    }

    /// <summary>Everyone with access to the repo (including the owner and organization admins), with their role.</summary>
    public async Task<IReadOnlyList<GitHubCollaborator>> GetCollaboratorsAsync(string owner, string repo, CancellationToken ct = default)
    {
        const string action = "list the vault's team";
        var people = new List<GitHubCollaborator>();
        for (var page = 1; page <= 10; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, $"/repos/{owner}/{repo}/collaborators?affiliation=all&per_page=100&page={page}", null, action, ct).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, action).ConfigureAwait(false);
            var items = json.RootElement.EnumerateArray().ToList();
            foreach (var item in items)
            {
                var role = item.TryGetProperty("role_name", out var r) ? r.GetString() ?? "" : "";
                people.Add(new GitHubCollaborator(item.GetProperty("login").GetString() ?? "", role));
            }
            if (items.Count < 100) break;
        }
        return people;
    }

    /// <summary>Invitations to the repo that haven't been accepted yet.</summary>
    public async Task<IReadOnlyList<GitHubInvitation>> GetInvitationsAsync(string owner, string repo, CancellationToken ct = default)
    {
        const string action = "list the vault's invitations";
        using var response = await SendAsync(HttpMethod.Get, $"/repos/{owner}/{repo}/invitations?per_page=100", null, action, ct).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, action).ConfigureAwait(false);
        return json.RootElement.EnumerateArray()
            .Where(i => i.TryGetProperty("invitee", out var who) && who.ValueKind == JsonValueKind.Object)
            .Select(i => new GitHubInvitation(
                i.GetProperty("id").GetInt64(),
                i.GetProperty("invitee").GetProperty("login").GetString() ?? "",
                i.TryGetProperty("created_at", out var at) && at.TryGetDateTimeOffset(out var created) ? created : (DateTimeOffset?)null,
                i.TryGetProperty("expired", out var expired) && expired.ValueKind == JsonValueKind.True))
            .ToList();
    }

    /// <summary>Invitations the signed-in user has received and not accepted yet (expired ones left out).</summary>
    public async Task<IReadOnlyList<GitHubRepoInvitation>> GetMyInvitationsAsync(CancellationToken ct = default)
    {
        const string action = "list your invitations";
        using var response = await SendAsync(HttpMethod.Get, "/user/repository_invitations?per_page=100", null, action, ct).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, action).ConfigureAwait(false);
        return json.RootElement.EnumerateArray()
            .Where(i => i.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object)
            .Select(i =>
            {
                var repo = i.GetProperty("repository");
                var fullName = repo.GetProperty("full_name").GetString() ?? "";
                var cloneUrl = repo.TryGetProperty("clone_url", out var c) && c.GetString() is { Length: > 0 } url ? url : $"https://github.com/{fullName}.git";
                return new GitHubRepoInvitation(
                    i.GetProperty("id").GetInt64(),
                    fullName,
                    cloneUrl,
                    repo.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                    i.TryGetProperty("inviter", out var who) && who.ValueKind == JsonValueKind.Object ? who.GetProperty("login").GetString() ?? "" : "",
                    i.TryGetProperty("expired", out var expired) && expired.ValueKind == JsonValueKind.True);
            })
            .Where(i => i.RepoFullName.IndexOf('/') > 0 && !i.Expired)
            .ToList();
    }

    /// <summary>Accepts an invitation the signed-in user received; the repo is theirs to use right after.</summary>
    public async Task AcceptInvitationAsync(GitHubRepoInvitation invitation, CancellationToken ct = default)
    {
        const string action = "accept the invitation";
        using var response = await SendAsync(new HttpMethod("PATCH"), $"/user/repository_invitations/{invitation.Id}", null, action, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw (int)response.StatusCode switch
        {
            404 => new PdmException($"The invitation to {invitation.RepoFullName} isn't there any more (it was withdrawn, already accepted, or expired). If you can't find the vault, ask {NameOrTeamLead(invitation)} to invite you again.", text),
            409 => new PdmException($"GitHub couldn't accept the invitation to {invitation.RepoFullName} right now. Try again in a minute.", text),
            401 => new PdmException("Your saved GitHub sign-in has expired. Sign in to GitHub again, then try again.", text),
            403 => new PdmException($"GitHub didn't let Jarvis accept the invitation. Accept it on https://github.com/{invitation.RepoFullName}/invitations instead. {ErrorMessageOf(text)}", text),
            var code => new PdmException($"GitHub couldn't accept the invitation ({code}: {ErrorMessageOf(text)}).", text),
        };
    }

    private static string NameOrTeamLead(GitHubRepoInvitation invitation) =>
        invitation.Inviter.Length > 0 ? invitation.Inviter : "your team lead";

    /// <summary>Takes away someone's access to the repo.</summary>
    public Task RemoveCollaboratorAsync(string owner, string repo, string user, CancellationToken ct = default) =>
        DeleteAsync($"/repos/{owner}/{repo}/collaborators/{Uri.EscapeDataString(user)}", $"remove {user} from the vault", ct);

    /// <summary>Withdraws an invitation that hasn't been accepted.</summary>
    public Task CancelInvitationAsync(string owner, string repo, long invitationId, CancellationToken ct = default) =>
        DeleteAsync($"/repos/{owner}/{repo}/invitations/{invitationId}", "cancel the invitation", ct);

    private async Task DeleteAsync(string path, string action, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, null, action, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return; // gone already is fine
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw (int)response.StatusCode == 403
            ? new PdmException($"GitHub didn't let you {action}: only people with the Admin role on the vault repo can. {ErrorMessageOf(text)}", text)
            : new PdmException($"GitHub couldn't {action} ({(int)response.StatusCode}: {ErrorMessageOf(text)}).", text);
    }

    /// <summary>GitHub's error text, including the first validation detail ("message" plus "errors[0].message").</summary>
    internal static string ErrorMessageOf(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var detail) && detail.GetString() is { Length: > 0 } d)
                    {
                        return message.Length > 0 ? $"{message} ({d})" : d;
                    }
                }
            }
            return message.Length > 0 ? message : body.Trim();
        }
        catch (JsonException)
        {
            return body.Trim();
        }
    }

    private static GitHubRepo ParseRepo(JsonElement repo)
    {
        bool Permission(string key) => repo.TryGetProperty("permissions", out var p) && p.TryGetProperty(key, out var v) && v.GetBoolean();
        return new GitHubRepo
        {
            FullName = repo.GetProperty("full_name").GetString() ?? "",
            CloneUrl = repo.TryGetProperty("clone_url", out var c) ? c.GetString() ?? "" : "",
            HtmlUrl = repo.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            IsPrivate = repo.TryGetProperty("private", out var p) && p.GetBoolean(),
            Size = repo.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
            CanPush = Permission("push") || Permission("admin"),
            IsAdmin = Permission("admin") || Permission("maintain"),
            CanInvite = Permission("admin"),
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string action)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PdmException($"GitHub couldn't {action} ({(int)response.StatusCode}: {ErrorMessageOf(text)}).", text);
        }
        return JsonDocument.Parse(text);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? jsonBody, string action, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ApiRoot + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.UserAgent.ParseAdd("DevilDrive");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (jsonBody != null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new VaultOfflineException(action, ex.Message);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new VaultOfflineException(action, ex.Message); // timeout
        }
    }
}

/// <summary>The account or organization already has a repo with this name.</summary>
public sealed class RepoNameTakenException : PdmException
{
    public RepoNameTakenException(string owner, string name)
        : base($"{owner} already has a repo named \"{name}\" on GitHub. Choose another name.")
    {
        Owner = owner;
        Name = name;
    }

    public string Owner { get; }
    public string Name { get; }
    public override string Title => "Name already taken";
}
