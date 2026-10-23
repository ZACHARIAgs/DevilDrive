using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Errors;
using Jarvis.Core.Notifications;
using Jarvis.Core.Vault;
using Xunit;

namespace Jarvis.Core.Tests;

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
    public async Task Snapshot_ShowsLocalAndVaultVersionCounts()
    {
        AliceChecksIn("shared.txt", "v2", "second");
        AliceChecksIn("shared.txt", "v3", "third");

        var vault = await Open(_bob);
        var before = await vault.GetSnapshotAsync(contactVault: true);
        var shared = before.Files.Single(f => f.RelativePath == "shared.txt");
        Assert.Equal("1/3", shared.VersionText);
        Assert.True(shared.IsOutdated);
        Assert.Equal("1/1", before.Files.Single(f => f.RelativePath == "notes.txt").VersionText);

        await vault.GetLatestAsync();
        var after = await vault.GetSnapshotAsync(contactVault: false);
        Assert.Equal("3/3", after.Files.Single(f => f.RelativePath == "shared.txt").VersionText);
    }

    [Fact]
    public async Task CheckIn_WorksWhileAnotherProgramHasTheFileLoaded_LikeSolidWorks()
    {
        Write(_bob, "notes.txt", "bob's notes");
        var vault = await Open(_bob);
        // SolidWorks keeps a shared handle on every loaded file (e.g. a part inside an open assembly).
        using (new FileStream(Path.Combine(_bob, "notes.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var result = await vault.CheckInAsync(new[] { "notes.txt" }, "checked in with the file loaded");
            Assert.Empty(result.Failures);
        }
    }

    [Fact]
    public async Task UseOlderVersion_StaysThroughGetLatest_CantBeCheckedIn_AndReturnsToLatest()
    {
        AliceChecksIn("shared.txt", "v2", "second");
        AliceChecksIn("shared.txt", "v3", "third");
        var vault = await Open(_bob);
        await vault.GetLatestAsync();

        var first = (await vault.GetHistoryAsync("shared.txt")).Single(v => v.Version == 1);
        await vault.UseVersionAsync("shared.txt", first);
        Assert.Equal("v1", Read(_bob, "shared.txt"));
        var file = (await vault.GetSnapshotAsync(contactVault: false)).Files.Single(f => f.RelativePath == "shared.txt");
        Assert.Equal("1/3", file.VersionText);
        Assert.Equal(FileState.Available, file.State);
        Assert.False(file.HasLocalChanges);
        await Assert.ThrowsAsync<PdmException>(() => vault.CheckInAsync(new[] { "shared.txt" }, "oops"));

        AliceChecksIn("shared.txt", "v4", "fourth");
        await vault.GetLatestAsync();
        Assert.Equal("v1", Read(_bob, "shared.txt"));
        Assert.Equal("1/4", (await vault.GetSnapshotAsync(contactVault: false)).Files.Single(f => f.RelativePath == "shared.txt").VersionText);

        await vault.ReturnToLatestAsync(new[] { "shared.txt" });
        Assert.Equal("v4", Read(_bob, "shared.txt"));
        Assert.Equal("4/4", (await vault.GetSnapshotAsync(contactVault: false)).Files.Single(f => f.RelativePath == "shared.txt").VersionText);
    }

    [Fact]
    public async Task VersionCopy_HoldsTheOldVersion_AndCantBeCheckedOut()
    {
        AliceChecksIn("shared.txt", "v2", "second");
        var vault = await Open(_bob);
        await vault.GetLatestAsync();
        var first = (await vault.GetHistoryAsync("shared.txt")).Single(v => v.Version == 1);

        var copy = await vault.CreateVersionCopyAsync("shared.txt", first);

        Assert.Equal(Path.Combine(_bob, "_versions", "shared v1.txt"), copy);
        Assert.Equal("v1", File.ReadAllText(copy));
        Assert.Equal("v2", Read(_bob, "shared.txt")); // the original is untouched
        Assert.Equal(copy, await vault.CreateVersionCopyAsync("shared.txt", first)); // reused, not made twice
        var result = await vault.CheckOutAsync(new[] { "_versions/shared v1.txt" });
        Assert.Contains("read-only copy of version 1", Assert.Single(result.Failures).Error.Message);
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

    // ------------------------------------------------------------ delete requests

    [Fact]
    public async Task DeleteRequest_IsSeenByEveryone_AndOnlyTheRequesterOrAnAdminTakesItBack()
    {
        var bob = await Open(_bob, login: "bob");
        var tooShort = await Assert.ThrowsAsync<PdmException>(() => bob.RequestDeleteAsync(new[] { "notes.txt" }, "old"));
        Assert.Contains("Write why it should be deleted", tooShort.Message);

        var result = await bob.RequestDeleteAsync(new[] { "notes.txt" }, "Replaced by the new notes, nothing uses it");
        Assert.Equal(new[] { "notes.txt" }, result.Succeeded);
        var again = await bob.RequestDeleteAsync(new[] { "notes.txt" }, "Asking a second time for no reason");
        Assert.Contains("already has a delete request", Assert.Single(again.Failures).Error.Message);

        var carol = await Open(_alice, login: "carol");
        await carol.GetLatestAsync();
        var seen = (await carol.GetSnapshotAsync(contactVault: false)).Files.Single(f => f.RelativePath == "notes.txt");
        Assert.Equal("bob", seen.DeleteRequest!.RequestedBy);
        Assert.Equal("Replaced by the new notes, nothing uses it", seen.DeleteRequest.Reason);
        var notHers = await carol.WithdrawDeleteRequestAsync(new[] { "notes.txt" });
        Assert.Contains("Only they or an admin", Assert.Single(notHers.Failures).Error.Message);
        await Assert.ThrowsAsync<PdmException>(() => carol.RejectDeleteRequestAsync(new[] { "notes.txt" }, "no"));

        var withdrawn = await bob.WithdrawDeleteRequestAsync(new[] { "notes.txt" });
        Assert.Equal(new[] { "notes.txt" }, withdrawn.Succeeded);
        Assert.Null((await bob.GetSnapshotAsync(contactVault: false)).Files.Single(f => f.RelativePath == "notes.txt").DeleteRequest);
    }

    [Fact]
    public async Task DeleteRequest_NotForFilesOutsideTheVault()
    {
        Write(_bob, "scratch.txt", "local only");
        var bob = await Open(_bob, login: "bob");

        var result = await bob.RequestDeleteAsync(new[] { "scratch.txt" }, "Made by mistake, not needed");

        Assert.Contains("Delete Local File", Assert.Single(result.Failures).Error.Message);
    }

    [Fact]
    public async Task DeleteFromVault_AdminDeletesForEveryone_AndClosesTheRequest()
    {
        var bob = await Open(_bob, login: "bob");
        await bob.RequestDeleteAsync(new[] { "notes.txt" }, "Replaced by the new notes, nothing uses it");
        var alice = await Open(_alice, login: "alice", repoAdmin: true);
        await alice.GetLatestAsync();

        var result = await alice.DeleteFromVaultAsync(new[] { "notes.txt", "never-there.txt" }, "");

        Assert.Equal(new[] { "notes.txt" }, result.Succeeded);
        Assert.Contains("isn't in the vault", Assert.Single(result.Failures).Error.Message);
        Assert.False(File.Exists(Path.Combine(_alice, "notes.txt")));

        await bob.GetLatestAsync();
        Assert.False(File.Exists(Path.Combine(_bob, "notes.txt")));
        var snapshot = await bob.GetSnapshotAsync(contactVault: false);
        Assert.DoesNotContain(snapshot.Files, f => f.RelativePath == "notes.txt");
        Assert.DoesNotContain("notes.txt", Read(_bob, ".pdm/delete-requests.json"));
    }

    [Fact]
    public async Task DeleteFromVault_OnlyAdmins()
    {
        var bob = await Open(_bob, login: "bob");
        var ex = await Assert.ThrowsAsync<PdmException>(() => bob.DeleteFromVaultAsync(new[] { "notes.txt" }, ""));
        Assert.Contains("Only vault admins", ex.Message);
        Assert.True(File.Exists(Path.Combine(_bob, "notes.txt")));
    }

    [Fact]
    public async Task DeleteRequest_AdminRejects_FileStays()
    {
        var bob = await Open(_bob, login: "bob");
        await bob.RequestDeleteAsync(new[] { "mine.txt" }, "I think this is a duplicate of notes");
        var alice = await Open(_alice, login: "alice", repoAdmin: true);
        await alice.GetLatestAsync();

        var result = await alice.RejectDeleteRequestAsync(new[] { "mine.txt" }, "It's still used for the report");

        Assert.Equal(new[] { "mine.txt" }, result.Succeeded);
        var file = (await alice.GetSnapshotAsync(contactVault: false)).Files.Single(f => f.RelativePath == "mine.txt");
        Assert.Null(file.DeleteRequest);
        Assert.True(File.Exists(Path.Combine(_alice, "mine.txt")));
    }

    /// <summary>bracket.sldasm (assembly) is used by frame.slddrw (drawing); both indexed.</summary>
    private void AliceAddsIndexedAssemblyAndDrawing()
    {
        Write(_alice, "bracket.sldasm", "bracket");
        Write(_alice, "frame.slddrw", "drawing");
        Directory.CreateDirectory(Path.Combine(_alice, ".pdm", "index"));
        File.WriteAllText(Path.Combine(_alice, ".pdm", "index", "bracket.sldasm.json"), "{\"references\":[]}");
        File.WriteAllText(Path.Combine(_alice, ".pdm", "index", "frame.slddrw.json"), "{\"references\":[\"bracket.sldasm\"]}");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "assembly and drawing, indexed");
        Git(_alice, "push");
    }

    [Fact]
    public async Task Delete_RefusesFilesOtherFilesUse()
    {
        AliceAddsIndexedAssemblyAndDrawing();
        var bob = await Open(_bob, login: "bob");
        await bob.GetLatestAsync();
        var alice = await Open(_alice, login: "alice", repoAdmin: true);

        var request = await bob.RequestDeleteAsync(new[] { "bracket.sldasm" }, "Replaced by the welded version");
        var delete = await alice.DeleteFromVaultAsync(new[] { "bracket.sldasm" }, "");

        Assert.Contains("is used in frame.slddrw", Assert.Single(request.Failures).Error.Message);
        Assert.Contains("is used in frame.slddrw", Assert.Single(delete.Failures).Error.Message);
        Assert.Empty(delete.Succeeded);
        Assert.True(File.Exists(Path.Combine(_alice, "bracket.sldasm")));
    }

    [Fact]
    public async Task Delete_AllowsAFileTogetherWithEverythingThatUsesIt()
    {
        AliceAddsIndexedAssemblyAndDrawing();
        var alice = await Open(_alice, login: "alice", repoAdmin: true);

        var result = await alice.DeleteFromVaultAsync(new[] { "bracket.sldasm", "frame.slddrw" }, "");

        Assert.Empty(result.Failures);
        Assert.False(File.Exists(Path.Combine(_alice, "bracket.sldasm")));
        Assert.False(File.Exists(Path.Combine(_alice, "frame.slddrw")));
    }

    [Fact]
    public async Task Delete_RefusesSolidWorksFiles_WhileAnAssemblyIsntIndexed()
    {
        AliceAddsAssemblyAndDrawing(); // not indexed: nobody knows what they use
        var alice = await Open(_alice, login: "alice", repoAdmin: true);

        var part = await alice.DeleteFromVaultAsync(new[] { "frame.sldprt" }, "");
        var text = await alice.DeleteFromVaultAsync(new[] { "notes.txt" }, "");

        Assert.Contains("Run Rebuild Index", Assert.Single(part.Failures).Error.Message);
        Assert.Contains("bracket.sldasm, frame.slddrw haven't been indexed", part.Failures[0].Error.Message);
        Assert.Equal(new[] { "notes.txt" }, text.Succeeded); // not a SolidWorks file: nothing references it
    }

    // ------------------------------------------------------------ moving files into folders

    /// <summary>Stands in for SolidWorks: which file uses which, and the reference rewrites it was asked for.</summary>
    private sealed class FakeSolidWorks : IReferenceEditor
    {
        public Dictionary<string, List<string>> Uses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Released { get; } = new();
        public bool Restored { get; private set; }
        public bool FailReplace { get; set; }

        public IReadOnlyList<string> GetReferences(string fullPath) =>
            Uses.TryGetValue(Path.GetFullPath(fullPath), out var list) ? list : new List<string>();

        public bool ReplaceReference(string referencingFile, string oldReference, string newReference)
        {
            if (FailReplace) return false;
            var list = Uses[Path.GetFullPath(referencingFile)];
            list[list.FindIndex(r => string.Equals(r, oldReference, StringComparison.OrdinalIgnoreCase))] = newReference;
            File.AppendAllText(referencingFile, " -> " + Path.GetFileName(Path.GetDirectoryName(newReference)));
            return true;
        }

        public void Release(IReadOnlyList<string> fullPaths) => Released.AddRange(fullPaths);

        public void Restore(IReadOnlyDictionary<string, string> movedFullPaths) => Restored = true;
    }

    private void AliceAddsAssemblyAndDrawing()
    {
        // .sldasm/.slddrw aren't lockable in this test vault (no lock server here), but they're SolidWorks files.
        Write(_alice, "bracket.sldasm", "bracket");
        Write(_alice, "frame.slddrw", "drawing");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "add assembly and drawing");
        Git(_alice, "push");
    }

    [Fact]
    public async Task Move_IntoANewFolder_ChecksInOneChange_ThatTeammatesGet()
    {
        var alice = await Open(_alice);
        var plan = await alice.PlanMoveAsync(VaultService.IntoFolder(new[] { "notes.txt", "mine.txt" }, "Docs/Old"), editor: null);

        Assert.True(plan.CanMove, string.Join(" ", plan.Problems));
        Assert.Empty(plan.Parents);
        var result = await alice.MoveAsync(plan, null, VaultService.MoveComment(plan.Moves));

        Assert.Empty(result.Failures);
        Assert.False(File.Exists(Path.Combine(_alice, "notes.txt")));
        Assert.Equal("v1", Read(_alice, "Docs/Old/notes.txt"));

        var bob = await Open(_bob);
        await bob.GetLatestAsync();
        Assert.Equal("v1", Read(_bob, "Docs/Old/mine.txt"));
        Assert.False(File.Exists(Path.Combine(_bob, "notes.txt")));
        var latest = (await bob.GetSnapshotAsync(contactVault: false)).Files.Single(f => f.RelativePath == "Docs/Old/notes.txt");
        Assert.Equal("Moved 2 files to Docs/Old", latest.LatestVersion!.Comment);
    }

    [Fact]
    public async Task Move_APartAnAssemblyUses_RepointsTheAssembly_InTheSameCheckIn()
    {
        AliceAddsAssemblyAndDrawing();
        var solidWorks = new FakeSolidWorks();
        solidWorks.Uses[Path.Combine(_alice, "frame.slddrw")] = new List<string> { Path.Combine(_alice, "bracket.sldasm") };
        var alice = await Open(_alice);

        var plan = await alice.PlanMoveAsync(VaultService.IntoFolder(new[] { "bracket.sldasm" }, "Frame"), solidWorks);
        Assert.True(plan.CanMove, string.Join(" ", plan.Problems));
        Assert.Equal(new[] { "frame.slddrw" }, plan.Parents);

        await alice.MoveAsync(plan, solidWorks, VaultService.MoveComment(plan.Moves));

        Assert.Equal("drawing -> Frame", Read(_alice, "frame.slddrw"));
        Assert.Equal(Path.Combine(_alice, "Frame", "bracket.sldasm"), solidWorks.Uses[Path.Combine(_alice, "frame.slddrw")].Single());
        Assert.Contains(Path.Combine(_alice, "frame.slddrw"), solidWorks.Released);
        Assert.True(solidWorks.Restored);
        var snapshot = await alice.GetSnapshotAsync(contactVault: false);
        Assert.All(new[] { "Frame/bracket.sldasm", "frame.slddrw" }, p => Assert.Equal(FileState.Available, snapshot.Files.Single(f => f.RelativePath == p).State));
        Assert.Equal("Moved bracket.sldasm to Frame", snapshot.Files.Single(f => f.RelativePath == "frame.slddrw").LatestVersion!.Comment);
    }

    [Fact]
    public async Task Move_PutsEverythingBack_WhenSolidWorksCantUpdateAReference()
    {
        AliceAddsAssemblyAndDrawing();
        var solidWorks = new FakeSolidWorks();
        solidWorks.Uses[Path.Combine(_alice, "frame.slddrw")] = new List<string> { Path.Combine(_alice, "bracket.sldasm") };
        var alice = await Open(_alice);
        var plan = await alice.PlanMoveAsync(VaultService.IntoFolder(new[] { "bracket.sldasm" }, "Frame"), solidWorks);

        solidWorks.FailReplace = true;
        var ex = await Assert.ThrowsAsync<PdmException>(() => alice.MoveAsync(plan, solidWorks, "move"));

        Assert.Contains("Nothing was moved", ex.Message);
        Assert.True(File.Exists(Path.Combine(_alice, "bracket.sldasm")));
        Assert.False(File.Exists(Path.Combine(_alice, "Frame", "bracket.sldasm")));
        Assert.True(solidWorks.Restored);
    }

    [Fact]
    public async Task Move_ExplainsWhatsInTheWay()
    {
        Directory.CreateDirectory(Path.Combine(_alice, "Docs"));
        Write(_alice, "Docs/notes.txt", "other notes");
        Write(_alice, "shared.txt", "edited");
        var alice = await Open(_alice);

        var sameName = await alice.PlanMoveAsync(VaultService.IntoFolder(new[] { "notes.txt" }, "Docs"), null);
        var edited = await alice.PlanMoveAsync(VaultService.IntoFolder(new[] { "shared.txt" }, "Docs"), null);
        var noSolidWorks = await alice.PlanMoveAsync(VaultService.IntoFolder(new[] { "frame.sldprt" }, "Frame"), null);

        Assert.Contains("already a file named notes.txt in Docs", Assert.Single(sameName.Problems));
        Assert.Contains("changes that aren't checked in", Assert.Single(edited.Problems));
        Assert.Contains("needs SolidWorks", Assert.Single(noSolidWorks.Problems));
        Assert.False(sameName.CanMove);
    }

    [Fact]
    public async Task Move_ANewFile_OnlyMovesItOnThisPc()
    {
        Write(_alice, "sketch.sldasm", "new");
        var alice = await Open(_alice);
        var plan = await alice.PlanMoveAsync(VaultService.IntoFolder(new[] { "sketch.sldasm" }, "Ideas"), new FakeSolidWorks());

        var result = await alice.MoveAsync(plan, new FakeSolidWorks(), "move");

        Assert.Equal("new", Read(_alice, "Ideas/sketch.sldasm"));
        Assert.Contains("only moved on this PC", Assert.Single(result.Notes));
        var snapshot = await alice.GetSnapshotAsync(contactVault: false);
        Assert.Equal(FileState.NotInVault, snapshot.Files.Single(f => f.RelativePath == "Ideas/sketch.sldasm").State);
    }

    [Fact]
    public async Task Folders_FromFilesAndFromThisPc_WithoutHiddenOnes()
    {
        Directory.CreateDirectory(Path.Combine(_alice, "Frame", "Welded"));
        Write(_alice, "Frame/Welded/tube.txt", "x");
        Directory.CreateDirectory(Path.Combine(_alice, ".pdm", "index"));
        Directory.CreateDirectory(Path.Combine(_alice, "Frame", "_versions"));
        var alice = await Open(_alice);

        var created = alice.CreateFolder("Frame", " Bolts ");
        var folders = alice.ListFolders(new[] { "Drivetrain/Gears/spur.sldprt", "notes.txt" });

        Assert.Equal("Frame/Bolts", created);
        Assert.Equal(new[] { "Drivetrain", "Drivetrain/Gears", "Frame", "Frame/Bolts", "Frame/Welded" }, folders);
        Assert.Throws<PdmException>(() => alice.CreateFolder("Frame", "bolts"));
    }

    [Theory]
    [InlineData("Frame", null)]
    [InlineData("  ", "Type a name")]
    [InlineData("a/b", "can't contain")]
    [InlineData("a:b", "can't contain")]
    [InlineData(".hidden", "start or end with a dot")]
    [InlineData("CON", "doesn't allow")]
    public void FolderNameProblem_SaysWhatsWrong(string name, string? problem)
    {
        var result = VaultService.FolderNameProblem(name);
        if (problem == null) Assert.Null(result);
        else Assert.Contains(problem, result);
    }

    [Fact]
    public async Task DeleteEmptyFolder_RefusesFoldersWithFiles()
    {
        var alice = await Open(_alice);
        alice.CreateFolder("", "Empty");
        alice.CreateFolder("Empty", "Inside");
        alice.CreateFolder("", "Full");
        Write(_alice, "Full/part.txt", "x");

        alice.DeleteEmptyFolder("Empty");
        var ex = Assert.Throws<PdmException>(() => alice.DeleteEmptyFolder("Full"));

        Assert.False(Directory.Exists(Path.Combine(_alice, "Empty")));
        Assert.Contains("isn't empty", ex.Message);
        Assert.True(File.Exists(Path.Combine(_alice, "Full", "part.txt")));
        Assert.Throws<PdmException>(() => alice.DeleteEmptyFolder(""));
    }

    [Fact]
    public async Task Move_RefusesWhenAnAssemblyThatIsntHereWasNeverIndexed()
    {
        AliceAddsAssemblyAndDrawing();
        File.Delete(Path.Combine(_alice, "bracket.sldasm")); // not on this PC, and nothing in the index says what it uses
        var alice = await Open(_alice);

        var plan = await alice.PlanMoveAsync(VaultService.IntoFolder(new[] { "frame.sldprt" }, "Frame"), new FakeSolidWorks());

        Assert.False(plan.CanMove);
        Assert.Contains(plan.Problems, p => p.StartsWith("bracket.sldasm isn't on this PC and Jarvis doesn't know which files it uses"));
        Assert.Contains(plan.Problems, p => p.Contains("Download it first"));
    }

    [Fact]
    public async Task Move_AllowsIt_WhenTheIndexSaysTheMissingAssemblyDoesntUseIt()
    {
        AliceAddsAssemblyAndDrawing();
        Directory.CreateDirectory(Path.Combine(_alice, ".pdm", "index"));
        File.WriteAllText(Path.Combine(_alice, ".pdm", "index", "bracket.sldasm.json"), "{\"references\":[\"other.sldprt\"]}");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "index");
        File.Delete(Path.Combine(_alice, "bracket.sldasm"));
        var alice = await Open(_alice);

        var plan = await alice.PlanMoveAsync(VaultService.IntoFolder(new[] { "frame.sldprt" }, "Frame"), new FakeSolidWorks());

        Assert.True(plan.CanMove, string.Join(" ", plan.Problems));
    }

    [Fact]
    public void MoveComment_SaysWhatHappened()
    {
        Assert.Equal("Moved a.sldprt to Frame/Parts", VaultService.MoveComment(new[] { new FileMove("a.sldprt", "Frame/Parts/a.sldprt") }));
        Assert.Equal("Moved a.sldprt to the top of the vault", VaultService.MoveComment(new[] { new FileMove("Frame/a.sldprt", "a.sldprt") }));
        Assert.Equal("Renamed Frame/a.sldprt to b.sldprt", VaultService.MoveComment(new[] { new FileMove("Frame/a.sldprt", "Frame/b.sldprt") }));
        Assert.Equal("Moved 2 files to Docs", VaultService.MoveComment(VaultService.IntoFolder(new[] { "x.txt", "Old/y.txt" }, "Docs")));
    }

    [Fact]
    public async Task DeleteLocalFiles_OnlyDeletesFilesNeverCheckedIn_AndKeepsACopy()
    {
        Write(_bob, "scrap part.sldprt", "made here");
        Write(_bob, "notes.txt", "edited");
        var bob = await Open(_bob);

        var result = await bob.DeleteLocalFilesAsync(new[] { "scrap part.sldprt", "notes.txt", "shared.txt" });

        Assert.Equal(new[] { "scrap part.sldprt" }, result.Succeeded);
        Assert.False(File.Exists(Path.Combine(_bob, "scrap part.sldprt")));
        Assert.Equal(new[] { "notes.txt", "shared.txt" }, result.Failures.Select(f => f.Path));
        Assert.All(result.Failures, f => Assert.Contains("is in the vault", f.Error.Message));
        Assert.Equal("edited", Read(_bob, "notes.txt")); // vault files are never touched
        var backup = Assert.Single(result.Notes);
        var folder = backup.Substring(backup.IndexOf(FileUtil.BackupRoot, StringComparison.Ordinal)).TrimEnd(',', ' ').Split(',')[0];
        Assert.Equal("made here", File.ReadAllText(Path.Combine(folder, "scrap part.sldprt")));
        Assert.DoesNotContain((await bob.GetSnapshotAsync(contactVault: false)).Files, f => f.RelativePath == "scrap part.sldprt");
    }

    [Fact]
    public async Task DeleteLocalFiles_RefusesAFileThatIsOpen()
    {
        Write(_bob, "open part.sldprt", "in SolidWorks");
        var bob = await Open(_bob);

        using (new FileStream(Path.Combine(_bob, "open part.sldprt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await bob.DeleteLocalFilesAsync(new[] { "open part.sldprt" });
            Assert.Empty(result.Succeeded);
            Assert.IsType<FileInUseException>(Assert.Single(result.Failures).Error);
        }
        Assert.True(File.Exists(Path.Combine(_bob, "open part.sldprt")));
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
    public async Task FileInReview_ShowsWhoSubmitted_AndCantBeCheckedOutOrIn()
    {
        Directory.CreateDirectory(Path.Combine(_alice, ".pdm"));
        Write(_alice, ".pdm/reviews.json",
            "{ \"reviews\": [ { \"path\": \"frame.sldprt\", \"submittedBy\": \"alice-gh\", \"submittedAt\": \"2026-10-19T10:00:00+00:00\", \"note\": \"Ready for release\" } ] }");
        Git(_alice, "add", "-A");
        Git(_alice, "commit", "-m", "Submitted frame for review");
        Git(_alice, "push", "-q");

        var bob = await Open(_bob);
        await bob.GetLatestAsync();
        var file = await bob.GetFileAsync("frame.sldprt");
        Assert.Equal(FileState.InReview, file!.State);
        Assert.Equal("alice-gh", file.ReviewSubmittedBy);
        Assert.Equal("Ready for release", file.Review!.Note);

        var checkOut = await bob.CheckOutAsync(new[] { "frame.sldprt" });
        Assert.Contains("in review", checkOut.Failures.Single().Error.Message);

        File.SetAttributes(Path.Combine(_bob, "frame.sldprt"), FileAttributes.Normal);
        Write(_bob, "frame.sldprt", "edited anyway");
        var checkIn = await Assert.ThrowsAsync<PdmException>(() => bob.CheckInAsync(new[] { "frame.sldprt" }, "sneaky"));
        Assert.Contains("in review", checkIn.Message);
    }

    [Fact]
    public async Task NonAdmin_CannotApproveOrReject()
    {
        var bob = await Open(_bob, "bob-gh");
        await Assert.ThrowsAsync<PdmException>(() => bob.ApproveReviewAsync(new[] { "frame.sldprt" }, "ok"));
        await Assert.ThrowsAsync<PdmException>(() => bob.RejectReviewAsync(new[] { "frame.sldprt" }, "no"));
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

        public Task<VaultIdentity?> GetIdentityAsync(Jarvis.Core.Git.IGitRunner git, string remoteUrl, CancellationToken ct) => Task.FromResult(_identity);
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
