using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Core.Git;

public sealed class GitResult
{
    public GitResult(IReadOnlyList<string> arguments, int exitCode, string stdOut, string stdErr)
    {
        Arguments = arguments;
        ExitCode = exitCode;
        StdOut = stdOut;
        StdErr = stdErr;
    }

    public IReadOnlyList<string> Arguments { get; }
    public int ExitCode { get; }
    public string StdOut { get; }
    public string StdErr { get; }
    public bool Success => ExitCode == 0;
    public string CommandText => "git " + string.Join(" ", Arguments);

    /// <summary>Everything useful for a "technical details" box.</summary>
    public string Describe() => $"> {CommandText}\n(exit code {ExitCode})\n{StdErr}\n{StdOut}".TrimEnd();
}

/// <summary>Runs git.exe. Abstracted so the vault logic can be tested with a fake.</summary>
public interface IGitRunner
{
    string WorkingDirectory { get; }

    Task<GitResult> RunAsync(IReadOnlyList<string> args, string? stdIn = null, System.TimeSpan? timeout = null, CancellationToken ct = default);
}
