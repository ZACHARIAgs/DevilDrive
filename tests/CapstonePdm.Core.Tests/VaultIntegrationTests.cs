using System;
using System.Collections.Generic;
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
        Git(_root, "clone", "-c", "core.autocrlf=false", remote, _alice);
        Configure(_alice, "Alice");
        File.WriteAllText(Path.Combine(_alice, ".gitattributes"), "*.sldprt lockable\n");
        Write(_alice, "shared.txt", "v1");
        Write(_alice, "mine.txt", "v1");
        Write(_alice, "notes.txt", "v1");
        Write(_alice, "frame.sldprt", "v1");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "first version");
        Git(_alice, "push", "-u", "origin", "main");

        Git(_root, "clone", "-c", "core.autocrlf=false", remote, _bob);
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

    [Fact]
    public async Task CheckInRequest_IsIgnored_WhenThatPersonNoLongerHasItCheckedOut()
    {
        Directory.CreateDirectory(Path.Combine(_alice, ".pdm"));
        Write(_alice, ".pdm/checkin-requests.json",
            "{ \"requests\": [ { \"path\": \"frame.sldprt\", \"owner\": \"bob-gh\", \"requestedBy\": \"alice-gh\", " +
            "\"requestedAt\": \"2026-10-19T10:00:00+00:00\", \"note\": \"Sam needs it\" } ] }");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "Requested check-in");
        Git(_alice, "push");

        // Nobody holds a lock on frame.sldprt (bob-gh already checked it in), so the request is moot.
        var bob = await Open(_bob);
        var file = await bob.GetFileAsync("frame.sldprt", contactVault: true);
        var snapshot = await bob.GetSnapshotAsync(contactVault: false);

        Assert.Null(file!.CheckInRequest);
        Assert.Equal(0, snapshot.CheckInRequestsForMe);
    }

    [Fact]
    public async Task NonAdmin_CannotRequestCheckIn()
    {
        var bob = await Open(_bob);
        var error = await Assert.ThrowsAsync<PdmException>(() => bob.RequestCheckInAsync("frame.sldprt", "please"));
        Assert.Contains("Only vault admins", error.Message);
    }

    [Fact]
    public async Task Index_GivesPropertiesAndWhereUsed()
    {
        Directory.CreateDirectory(Path.Combine(_alice, ".pdm", "index"));
        Write(_alice, ".pdm/index/robot.sldasm.json",
            "{ \"savedIn\": 2024, \"references\": [ \"frame.sldprt\" ], \"externalReferences\": [ \"D:\\\\Downloads\\\\bolt.sldprt\" ], " +
            "\"properties\": { \"PartNo\": \"CAP-100\", \"Description\": \"Robot\" } }");
        Write(_alice, "robot.sldasm", "asm");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "Robot assembly");
        Git(_alice, "push");

        var bob = await Open(_bob);
        await bob.GetLatestAsync();
        var snapshot = await bob.GetSnapshotAsync(contactVault: false);
        var robot = snapshot.Files.Single(f => f.RelativePath == "robot.sldasm");
        var frame = snapshot.Files.Single(f => f.RelativePath == "frame.sldprt");

        Assert.Equal("CAP-100", robot.Index!.GetProperty(IndexEntry.PartNumberNames));
        Assert.Equal(2024, robot.Index.SavedIn);
        Assert.Single(robot.Index.ExternalReferences);
        Assert.Equal(new[] { "robot.sldasm" }, frame.UsedIn);
    }

    [Fact]
    public async Task CheckIn_CommitsIndexEntry_WithTheFile()
    {
        Write(_bob, "notes.txt", "bob's notes");
        var bob = await Open(_bob);
        var entry = new IndexEntry { SavedIn = 2024 };
        entry.Properties["Description"] = "Test notes";
        await bob.CheckInAsync(new[] { "notes.txt" }, "notes with index", indexUpdates: new Dictionary<string, IndexEntry?> { ["notes.txt"] = entry });

        var alice = await Open(_alice);
        await alice.GetLatestAsync();
        var notes = await alice.GetFileAsync("notes.txt");
        Assert.Equal("Test notes", notes!.Index!.GetProperty(IndexEntry.DescriptionNames));
        Assert.Equal("Bob", notes.Index.UpdatedBy);
    }

    [Fact]
    public async Task Milestone_ShowsOldFiles_IsReadOnly_AndReturnsToLatest()
    {
        // Alice is an admin.
        Write(_alice, "pdm.json", "{ \"admins\": [ \"alice-gh\" ] }");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "Admins");
        Git(_alice, "push");
        var alice = await Open(_alice, "alice-gh", repoAdmin: true);
        var milestone = await alice.CreateMilestoneAsync("Design Review 1", "Before the redesign");
        Assert.Equal("milestone/design-review-1", milestone.Tag);
        AliceChecksIn("shared.txt", "v2", "after the review");

        var bob = await Open(_bob);
        await bob.GetLatestAsync();
        var listed = (await bob.GetMilestonesAsync()).Single();
        Assert.Equal("Design Review 1", listed.Name);
        Assert.Equal("Before the redesign", listed.Description);

        await bob.ViewMilestoneAsync(listed.Tag);
        Assert.Equal("v1", Read(_bob, "shared.txt"));
        Assert.Equal("Design Review 1", (await bob.GetSnapshotAsync(contactVault: false)).ViewingMilestone!.Name);
        Write(_bob, "notes.txt", "edit while viewing");
        await Assert.ThrowsAsync<ViewingMilestoneException>(() => bob.CheckInAsync(new[] { "notes.txt" }, "should fail"));
        Git(_bob, "checkout", "--", "notes.txt");

        await bob.ReturnToLatestAsync();
        Assert.Equal("v2", Read(_bob, "shared.txt"));
        Assert.Null((await bob.GetSnapshotAsync(contactVault: false)).ViewingMilestone);
    }

    [Fact]
    public async Task CheckIn_OfAMove_RemovesOldPath_AddsNewPath_AndMovesTheIndexEntry()
    {
        var bob = await Open(_bob);
        Directory.CreateDirectory(Path.Combine(_bob, "docs"));
        File.Move(Path.Combine(_bob, "notes.txt"), Path.Combine(_bob, "docs", "notes.txt"));

        var index = new Dictionary<string, IndexEntry?> { ["notes.txt"] = null, ["docs/notes.txt"] = new IndexEntry { SavedIn = 2024 } };
        await bob.CheckInAsync(new[] { "notes.txt", "docs/notes.txt" }, "Moved notes into docs", indexUpdates: index);

        var alice = await Open(_alice);
        await alice.GetLatestAsync();
        Assert.False(File.Exists(Path.Combine(_alice, "notes.txt")));
        Assert.Equal("v1", Read(_alice, "docs/notes.txt"));
        Assert.Equal(2024, (await alice.GetFileAsync("docs/notes.txt"))!.Index!.SavedIn);
        // History is per path: the move is the file's first version at its new location.
        Assert.Single(await alice.GetHistoryAsync("docs/notes.txt"));
    }

    [Fact]
    public async Task TypingAnAdminsUsername_DoesNotGiveAdminRights()
    {
        Write(_alice, "pdm.json", "{ \"admins\": [ \"alice-gh\" ] }");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "Admins");
        Git(_alice, "push");
        Git(_bob, "config", "pdm.githubLogin", "alice-gh"); // the old, typed setting

        var unverified = await Open(_bob);
        await unverified.GetLatestAsync();
        Assert.False(unverified.IsAdmin);

        var verifiedBob = await Open(_bob, "bob-gh");
        Assert.False(verifiedBob.IsAdmin);
        Assert.Equal("bob-gh", verifiedBob.GitHubLogin);

        // Being listed in pdm.json no longer counts: only GitHub roles do.
        var listedAlice = await Open(_alice, "alice-gh");
        Assert.False(listedAlice.IsAdmin);
        var repoAdminAlice = await Open(_alice, "alice-gh", repoAdmin: true);
        Assert.True(repoAdminAlice.IsAdmin);
    }

    [Fact]
    public async Task UpdateVaultSetup_InstallsGuard_AndClearsTheProblems()
    {
        var admin = await Open(_alice, "alice-gh", repoAdmin: true);
        var before = await admin.GetSnapshotAsync(contactVault: true);
        Assert.Contains(before.SetupProblems, p => p.Contains("vault guard isn't installed"));

        await admin.UpdateVaultSetupAsync();

        var after = await admin.GetSnapshotAsync(contactVault: true);
        Assert.Empty(after.SetupProblems);
        Assert.True(File.Exists(Path.Combine(_alice, ".github", "workflows", "vault-guard.yml")));
        Assert.Contains(".pdm/preview/**", Read(_alice, ".gitattributes"));

        var bob = await Open(_bob);
        Assert.Empty((await bob.GetSnapshotAsync(contactVault: true)).SetupProblems); // only admins are told
    }

    [Fact]
    public async Task CheckIn_IncludesPreview_EvenWhenSolidWorksChangedItsLetterCase()
    {
        // SolidWorks saves "x.STL" when asked for "x.stl"; Windows doesn't mind but Git does.
        Write(_bob, "notes.txt", "bob's notes");
        Directory.CreateDirectory(Path.Combine(_bob, ".pdm", "preview"));
        File.WriteAllText(Path.Combine(_bob, ".pdm", "preview", "notes.txt.STL"), "solid preview");

        var bob = await Open(_bob);
        await bob.CheckInAsync(new[] { "notes.txt" }, "notes with a 3D preview", previews: new[] { VaultPaths.PreviewFor("notes.txt") });

        var alice = await Open(_alice);
        await alice.GetLatestAsync();
        Assert.True(File.Exists(Path.Combine(_alice, ".pdm", "preview", "notes.txt.STL")));
        Assert.Equal("bob's notes", Read(_alice, "notes.txt"));
    }

    [Fact]
    public async Task StorageMeter_CountsEveryStoredVersion()
    {
        // Two versions of a Git LFS file, committed locally (no push needed to measure).
        File.AppendAllText(Path.Combine(_alice, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        File.WriteAllBytes(Path.Combine(_alice, "part.bin"), new byte[3000]);
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "v1");
        File.WriteAllBytes(Path.Combine(_alice, "part.bin"), new byte[5000]);
        Git(_alice, "commit", "-am", "v2");

        var alice = await Open(_alice);
        var usage = await alice.GetStorageUsageAsync();

        Assert.Equal(2, usage.VersionCount);
        Assert.Equal(8000, usage.AllVersionsBytes);
        Assert.Equal(5000, usage.LatestBytes);
        Assert.Equal(10, usage.LimitGB);
    }

    [Fact]
    public async Task ExportWarning_OnlyForNewOrChangedExports_AndCanBeTurnedOff()
    {
        Write(_bob, "plate.dxf", "outline");
        var bob = await Open(_bob);

        var warning = await bob.GetExportWarningAsync(new[] { "plate.dxf", "notes.txt" });
        Assert.NotNull(warning);
        Assert.Contains("plate.dxf", warning);
        Assert.Null(await bob.GetExportWarningAsync(new[] { "notes.txt" }));

        Write(_bob, "pdm.json", "{ \"warnOnExports\": false }");
        var quiet = await Open(_bob);
        Assert.Null(await quiet.GetExportWarningAsync(new[] { "plate.dxf" }));
    }

    [Fact]
    public async Task DownloadFolders_AreSaved_AndCanBeReset()
    {
        var bob = await Open(_bob);
        Assert.Null(await bob.GetDownloadFoldersAsync());

        await bob.SetDownloadFoldersAsync(new[] { "Chassis", "Drivetrain\\" });
        Assert.Equal(new[] { "Chassis", "Drivetrain" }, await bob.GetDownloadFoldersAsync());
        Assert.Equal(new[] { "Chassis", "Drivetrain" }, (await bob.GetSnapshotAsync(contactVault: false)).DownloadFolders);

        await bob.SetDownloadFoldersAsync(null);
        Assert.Null(await bob.GetDownloadFoldersAsync());
    }

    [Fact]
    public async Task ChangingTheGuardWithGit_PostsOneAlert()
    {
        var admin = await Open(_alice, "alice-gh", repoAdmin: true);
        await admin.UpdateVaultSetupAsync();

        // A teammate switches the guard off with Git commands.
        Git(_bob, "pull", "-q");
        File.WriteAllText(Path.Combine(_bob, ".github", "workflows", "vault-guard.yml"), "name: nothing to see\non: workflow_dispatch\n");
        Git(_bob, "commit", "-qam", "harmless tweak");
        Git(_bob, "push", "-q");

        var notifier = new RecordingNotifier();
        var anyone = await Open(_alice, notifier: notifier);
        await anyone.GetSnapshotAsync(contactVault: true);
        await anyone.GetSnapshotAsync(contactVault: true);

        var alert = Assert.Single(notifier.Events, e => e.Kind == VaultEventKind.GuardTampered);
        Assert.Equal("changed", alert.Comment);
        Assert.Equal("Bob", alert.PreviousOwner);
    }

    [Fact]
    public async Task MilestoneOnASideCommit_IsIgnored()
    {
        Git(_bob, "checkout", "-q", "--orphan", "side");
        Write(_bob, "fake.txt", "not the real vault");
        Git(_bob, "add", "-A");
        Git(_bob, "commit", "-qm", "side commit");
        Git(_bob, "tag", "-a", "milestone/fake", "-m", "Fake Release");
        Git(_bob, "push", "-q", "origin", "milestone/fake");
        Git(_bob, "checkout", "-q", "-f", "main");

        var alice = await Open(_alice);
        Assert.Empty(await alice.GetMilestonesAsync());
    }

    [Fact]
    public async Task Drawing_IsFlaggedOutOfDate_WhenItsPartChangesLater()
    {
        // A drawing of frame.sldprt, recorded in the index.
        Directory.CreateDirectory(Path.Combine(_alice, ".pdm", "index"));
        Write(_alice, "frame.slddrw", "drawing v1");
        Write(_alice, ".pdm/index/frame.slddrw.json", "{ \"references\": [ \"frame.sldprt\" ] }");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "Drawing of the frame");
        Git(_alice, "push", "-q");

        var bob = await Open(_bob);
        await bob.GetLatestAsync();
        var before = await bob.GetSnapshotAsync(contactVault: false);
        Assert.Equal(new[] { "frame.slddrw" }, before.Files.Single(f => f.RelativePath == "frame.sldprt").Drawings);
        Assert.Empty(before.Files.Single(f => f.RelativePath == "frame.slddrw").ChangedSinceDrawing);

        // The part changes afterwards; the drawing doesn't.
        Write(_alice, "frame.sldprt", "v2");
        Git(_alice, "commit", "-qam", "Thicker frame", "--date", DateTime.Now.AddDays(1).ToString("s"));
        Git(_alice, "push", "-q");
        await bob.GetLatestAsync();
        var after = await bob.GetSnapshotAsync(contactVault: false);

        Assert.Equal(new[] { "frame.sldprt" }, after.Files.Single(f => f.RelativePath == "frame.slddrw").ChangedSinceDrawing);
        Assert.Equal(new[] { "frame.slddrw" }, after.Files.Single(f => f.RelativePath == "frame.sldprt").StaleDrawings);
    }

    [Fact]
    public async Task GitHubRepoAdmin_IsAlwaysAnAdmin()
    {
        var owner = await Open(_bob, "owner-gh", repoAdmin: true);
        Assert.True(owner.IsAdmin);
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

    /// <summary>Opens a clone as a given GitHub account (null = not verified, as when offline).</summary>
    private static Task<VaultService> Open(string path, string? login = null, bool repoAdmin = false, INotifier? notifier = null) =>
        VaultService.OpenAsync(path, notifier ?? new NoNotifier(), identityProvider: new FakeIdentity(login == null ? null : new VaultIdentity(login, repoAdmin)));

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

    private sealed class FakeIdentity : IIdentityProvider
    {
        private readonly VaultIdentity? _identity;

        public FakeIdentity(VaultIdentity? identity) => _identity = identity;

        public Task<VaultIdentity?> GetIdentityAsync(CapstonePdm.Core.Git.IGitRunner git, string remoteUrl, CancellationToken ct) => Task.FromResult(_identity);
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<VaultEvent> Events { get; } = new List<VaultEvent>();

        public Task NotifyAsync(VaultEvent e, CancellationToken ct = default)
        {
            Events.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class NoNotifier : INotifier
    {
        public Task NotifyAsync(VaultEvent e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
