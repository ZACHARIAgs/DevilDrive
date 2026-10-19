using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CapstonePdm.Core.Errors;
using CapstonePdm.Core.Notifications;
using CapstonePdm.Core.Vault;
using Xunit;

namespace CapstonePdm.Core.Tests;

/// <summary>
/// Runs the real vault logic against real Git: a local bare repo stands in for GitHub, with two
/// clones for two teammates. A local remote has no LFS lock server, so these tests use
/// non-lockable files for check-in. Locking is covered by the parser and classifier tests.
/// </summary>
public sealed class VaultIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "capstone-pdm-tests", Guid.NewGuid().ToString("N"));
    private readonly string _alice;
    private readonly string _bob;

    public VaultIntegrationTests()
    {
        var remote = Path.Combine(_root, "remote.git");
        _alice = Path.Combine(_root, "alice");
        _bob = Path.Combine(_root, "bob");
        Directory.CreateDirectory(_root);

        Git(_root, "init", "--bare", "--initial-branch=main", remote);
        Git(_root, "clone", remote, _alice);
        Configure(_alice, "Alice");
        File.WriteAllText(Path.Combine(_alice, ".gitattributes"), "*.sldprt lockable\n");
        Write(_alice, "shared.txt", "v1");
        Write(_alice, "mine.txt", "v1");
        Write(_alice, "notes.txt", "v1");
        Write(_alice, "frame.sldprt", "v1");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "first version");
        Git(_alice, "push", "-u", "origin", "main");

        Git(_root, "clone", remote, _bob);
        Configure(_bob, "Bob");
    }

    [Fact]
    public async Task GetLatest_UpdatesOtherFiles_AndNeverTouchesMyEdits()
    {
        Write(_bob, "mine.txt", "bob's unsaved work");
        AliceChecksIn("shared.txt", "v2", "updated shared");

        var vault = await Open(_bob);
        var result = await vault.GetLatestAsync();

        Assert.Equal(1, result.CheckInsReceived);
        Assert.Equal("v2", Read(_bob, "shared.txt"));
        Assert.Equal("bob's unsaved work", Read(_bob, "mine.txt"));
    }

    [Fact]
    public async Task GetLatest_WhenVaultChangedMyEditedFile_StopsWithoutOverwriting()
    {
        Write(_bob, "shared.txt", "bob's version");
        AliceChecksIn("shared.txt", "v2", "alice's change");

        var vault = await Open(_bob);
        var conflict = await Assert.ThrowsAsync<VaultConflictException>(() => vault.GetLatestAsync());

        Assert.Contains("shared.txt", conflict.Paths);
        Assert.Equal("bob's version", Read(_bob, "shared.txt"));
    }

    [Fact]
    public async Task CheckIn_SendsNewVersion_AndOthersGetIt_EvenWhenVaultMovedOn()
    {
        AliceChecksIn("shared.txt", "v2", "alice's change");
        Write(_bob, "notes.txt", "bob's notes");

        var bob = await Open(_bob);
        var result = await bob.CheckInAsync(new[] { "notes.txt" }, "wrote up test results");
        Assert.Equal(new[] { "notes.txt" }, result.Succeeded);
        Assert.Equal("v2", Read(_bob, "shared.txt"));

        var alice = await Open(_alice);
        await alice.GetLatestAsync();
        Assert.Equal("bob's notes", Read(_alice, "notes.txt"));

        var history = await alice.GetHistoryAsync("notes.txt");
        Assert.Equal(2, history.Count);
        Assert.Equal("wrote up test results", history[0].Comment);
        Assert.Equal("Bob", history[0].Author);
        Assert.Equal(2, history[0].Version);
    }

    [Fact]
    public async Task CheckIn_RequiresComment()
    {
        Write(_bob, "notes.txt", "x");
        var bob = await Open(_bob);
        await Assert.ThrowsAsync<PdmException>(() => bob.CheckInAsync(new[] { "notes.txt" }, "   "));
    }

    [Fact]
    public async Task CheckIn_OnlyCommitsTheChosenFiles()
    {
        Write(_bob, "notes.txt", "bob's notes");
        Write(_bob, "mine.txt", "not ready yet");

        var bob = await Open(_bob);
        await bob.CheckInAsync(new[] { "notes.txt" }, "notes only");

        var file = await bob.GetFileAsync("mine.txt");
        Assert.Equal(FileState.Modified, file!.State);
        Assert.Equal("not ready yet", Read(_bob, "mine.txt"));
    }

    [Fact]
    public async Task Snapshot_ReportsStates()
    {
        Write(_bob, "new part.sldprt", "new");
        Write(_bob, "notes.txt", "edited");
        Write(_bob, "bracket.sldprt", "x");
        AliceChecksIn("shared.txt", "v2", "alice's change");

        var bob = await Open(_bob);
        var snapshot = await bob.GetSnapshotAsync(contactVault: true);
        FileState StateOf(string name) => snapshot.Files.Single(f => f.RelativePath == name).State;

        Assert.Equal(1, snapshot.UpdatesAvailable);
        Assert.Equal(FileState.NotInVault, StateOf("new part.sldprt"));
        Assert.True(snapshot.Files.Single(f => f.RelativePath == "new part.sldprt").IsLockable);
        Assert.Equal(FileState.Modified, StateOf("notes.txt"));
        Assert.True(snapshot.Files.Single(f => f.RelativePath == "shared.txt").IsOutdated);
        Assert.Equal("first version", snapshot.Files.Single(f => f.RelativePath == "mine.txt").LatestVersion!.Comment);
        Assert.DoesNotContain(snapshot.Files, f => f.RelativePath == ".gitattributes");
    }

    [Fact]
    public async Task UndoCheckOut_OfEditedFile_RestoresVaultVersion_AndKeepsBackup()
    {
        Write(_bob, "notes.txt", "mistake");
        var bob = await Open(_bob);

        var result = await bob.UndoCheckOutAsync(new[] { "notes.txt" });

        Assert.Equal("v1", Read(_bob, "notes.txt"));
        Assert.Contains(result.Notes, n => n.Contains("backed up"));
    }

    [Fact]
    public async Task FrozenFile_ShowsAsFrozen_WithWhoAndWhy()
    {
        AliceFreezes("frame.sldprt", "Released for production, rev A");

        var bob = await Open(_bob);
        var file = await bob.GetFileAsync("frame.sldprt", contactVault: true);

        Assert.Equal(FileState.Frozen, file!.State);
        Assert.Equal("alice-gh", file.FrozenBy);
        Assert.Equal("Released for production, rev A", file.Freeze!.Reason);
    }

    [Fact]
    public async Task FrozenFile_CannotBeCheckedOut()
    {
        AliceFreezes("frame.sldprt", "Released");

        var bob = await Open(_bob);
        var result = await bob.CheckOutAsync(new[] { "frame.sldprt" });

        Assert.Empty(result.Succeeded);
        Assert.IsType<FrozenFileException>(result.Failures.Single().Error);
    }

    [Fact]
    public async Task FrozenFile_CannotBeCheckedIn_EvenIfEditedAnyway()
    {
        AliceFreezes("frame.sldprt", "Released");
        var bob = await Open(_bob);
        await bob.GetLatestAsync();
        File.SetAttributes(Path.Combine(_bob, "frame.sldprt"), FileAttributes.Normal);
        Write(_bob, "frame.sldprt", "sneaky edit");

        await Assert.ThrowsAsync<FrozenFileException>(() => bob.CheckInAsync(new[] { "frame.sldprt" }, "changed it anyway"));
    }

    [Fact]
    public async Task NonAdmin_CannotChangeTeamSettings_OrFreeze()
    {
        Write(_bob, "pdm.json", "{ \"admins\": [ \"bob\" ] }");
        var bob = await Open(_bob);

        var settings = await Assert.ThrowsAsync<PdmException>(() => bob.CheckInAsync(new[] { "pdm.json" }, "make me admin"));
        Assert.Contains("Only vault admins", settings.Message);

        var freeze = await Assert.ThrowsAsync<PdmException>(() => bob.FreezeAsync(new[] { "frame.sldprt" }, "nope"));
        Assert.Contains("Only vault admins", freeze.Message);
    }

    // ------------------------------------------------------------ helpers

    /// <summary>What an admin's Freeze records in the vault (the lock part needs a real LFS server).</summary>
    private void AliceFreezes(string file, string reason)
    {
        Directory.CreateDirectory(Path.Combine(_alice, ".pdm"));
        Write(_alice, ".pdm/frozen.json",
            "{ \"files\": [ { \"path\": \"" + file + "\", \"by\": \"alice-gh\", \"at\": \"2026-10-19T10:00:00+00:00\", \"reason\": \"" + reason + "\" } ] }");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "Froze " + file);
        Git(_alice, "push");
    }

    private void AliceChecksIn(string file, string content, string comment)
    {
        Write(_alice, file, content);
        Git(_alice, "commit", "-am", comment);
        Git(_alice, "push");
    }

    private static Task<VaultService> Open(string path) => VaultService.OpenAsync(path, new NoNotifier());

    private static void Configure(string repo, string name)
    {
        Git(repo, "config", "user.name", name);
        Git(repo, "config", "user.email", name.ToLowerInvariant() + "@example.com");
        Git(repo, "config", "core.autocrlf", "false");
    }

    private static void Write(string repo, string file, string content) => File.WriteAllText(Path.Combine(repo, file), content);

    private static string Read(string repo, string file) => File.ReadAllText(Path.Combine(repo, file));

    private static void Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr}");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp folder; leftovers are harmless.
        }
    }

    private sealed class NoNotifier : INotifier
    {
        public Task NotifyAsync(VaultEvent e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
