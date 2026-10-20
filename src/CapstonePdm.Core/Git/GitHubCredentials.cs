using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CapstonePdm.Core.Git;

/// <summary>The GitHub token Git's credential manager saved when the user signed in (never prompts).</summary>
public static class GitHubCredentials
{
    public static async Task<string?> GetTokenAsync(CancellationToken ct = default, string host = "github.com")
    {
        var gitExe = GitRunner.FindGit();
        if (gitExe == null) return null;
        var git = new GitRunner(Path.GetTempPath(), gitExe);
        var fill = await git.RunAsync(new[] { "-c", "credential.interactive=never", "credential", "fill" },
            $"protocol=https\nhost={host}\n\n", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!fill.Success) return null;
        var token = fill.StdOut.Split('\n').Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith("password=", StringComparison.Ordinal))?.Substring("password=".Length);
        return string.IsNullOrEmpty(token) ? null : token;
    }
}
