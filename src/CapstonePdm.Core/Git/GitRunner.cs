using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CapstonePdm.Core.Errors;

namespace CapstonePdm.Core.Git;

/// <summary>Runs git.exe as a child process with UTF-8 I/O, no console window and no terminal prompts.</summary>
public sealed class GitRunner : IGitRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    // Prepended to every command: raw (unquoted) non-ASCII paths, and no color codes in output.
    private static readonly string[] GlobalArgs = { "-c", "core.quotepath=off", "-c", "color.ui=never" };

    private readonly string _gitExe;

    public GitRunner(string workingDirectory, string gitExecutable)
    {
        WorkingDirectory = workingDirectory;
        _gitExe = gitExecutable;
    }

    public string WorkingDirectory { get; }

    /// <summary>
    /// Finds git.exe on PATH or in the usual Git for Windows install folders. The fallback matters
    /// right after setup, when a running app still has the old PATH.
    /// </summary>
    public static string? FindGit()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "git.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; skip it.
            }
        }

        var fallbacks = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe"),
        };
        return fallbacks.FirstOrDefault(File.Exists);
    }

    public async Task<GitResult> RunAsync(IReadOnlyList<string> args, string? stdIn = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_gitExe, CommandLine.Join(GlobalArgs.Concat(args)))
        {
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        // Never hang waiting for a console prompt nobody can see. Git Credential Manager still
        // shows its own sign-in window when needed.
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        // English messages, so GitErrorClassifier can recognize them.
        psi.EnvironmentVariables["LC_ALL"] = "C";
        psi.EnvironmentVariables["LANG"] = "C";

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(true);

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new GitNotFoundException();
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        if (stdIn != null)
        {
            // Write raw UTF-8 bytes: the default stdin encoding is the console code page.
            var bytes = Utf8NoBom.GetBytes(stdIn);
            await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        }
        process.StandardInput.Close();

        var limit = timeout ?? DefaultTimeout;
        using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var finished = await Task.WhenAny(exited.Task, Task.Delay(limit, delayCts.Token)).ConfigureAwait(false);
            delayCts.Cancel();
            if (finished != exited.Task && !process.HasExited)
            {
                KillTree(process);
                ct.ThrowIfCancellationRequested();
                throw new GitCommandException(
                    $"Git took longer than {limit.TotalMinutes:0.#} minutes and was stopped. If your connection is slow, try again.",
                    "> git " + string.Join(" ", args));
            }
        }

        process.WaitForExit();
        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);
        return new GitResult(args, process.ExitCode, stdOut, stdErr);
    }

    private static void KillTree(Process process)
    {
        // Process.Kill(entireProcessTree) isn't in netstandard2.0; git spawns helpers (git-lfs, git-remote-https).
        try
        {
            using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/PID {process.Id} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            killer?.WaitForExit(5000);
        }
        catch (Exception)
        {
            // Fall through to Kill().
        }

        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
