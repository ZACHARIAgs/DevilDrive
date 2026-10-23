using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Errors;

namespace Jarvis.Core.Vault;

/// <summary>
/// What moving SolidWorks files needs from SolidWorks: assemblies and drawings store the full path of
/// every file they use, and only SolidWorks can read and rewrite those references. The add-in uses
/// the SolidWorks it runs in; the desktop app starts SolidWorks in the background.
/// </summary>
public interface IReferenceEditor
{
    /// <summary>Files <paramref name="fullPath"/> directly uses (full paths). Works on closed files.</summary>
    IReadOnlyList<string> GetReferences(string fullPath);

    /// <summary>Points a closed file at a moved file. False if SolidWorks couldn't.</summary>
    bool ReplaceReference(string referencingFile, string oldReference, string newReference);

    /// <summary>
    /// Called after check-out, before anything moves: make sure nothing has these files open (the add-in
    /// saves and closes them; the app refuses if they're open).
    /// </summary>
    void Release(IReadOnlyList<string> fullPaths);

    /// <summary>Called at the end, also after a failure: reopen what <see cref="Release"/> closed, at their new paths.</summary>
    void Restore(IReadOnlyDictionary<string, string> movedFullPaths);
}

/// <summary>One file to move or rename (vault-relative paths).</summary>
public sealed class FileMove
{
    public FileMove(string from, string to)
    {
        From = from;
        To = to;
    }

    public string From { get; }
    public string To { get; }
}

/// <summary>What a move will do, worked out before anything changes, so the user can confirm it.</summary>
public sealed class MovePlan
{
    public List<FileMove> Moves { get; } = new List<FileMove>();
    /// <summary>Files that use a moved file: their references get updated and they're checked in too.</summary>
    public List<string> Parents { get; } = new List<string>();
    /// <summary>Why it can't be done (empty if it can).</summary>
    public List<string> Problems { get; } = new List<string>();
    public bool CanMove => Problems.Count == 0 && Moves.Count > 0;
}

public sealed partial class VaultService
{
    private static readonly string[] SolidWorksExtensions = { ".sldprt", ".sldasm", ".slddrw" };

    public static bool IsSolidWorksFile(string path) =>
        SolidWorksExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Destination paths for moving files into a folder ("" is the top of the vault), keeping their names.</summary>
    public static IReadOnlyList<FileMove> IntoFolder(IEnumerable<string> relativePaths, string folder)
    {
        var target = folder.Replace('\\', '/').Trim('/');
        return relativePaths.Select(p =>
        {
            var name = PathList.Name(p);
            return new FileMove(p, target.Length == 0 ? name : target + "/" + name);
        }).ToList();
    }

    /// <summary>
    /// Checks a move: every file can be moved, nothing is in the way, and finds the files that use the moved
    /// ones (with <paramref name="editor"/>, reading each SolidWorks file on this PC; plus the vault index
    /// for files that aren't downloaded here). Changes nothing.
    /// </summary>
    public async Task<MovePlan> PlanMoveAsync(IEnumerable<FileMove> moves, IReferenceEditor? editor, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var plan = new MovePlan();
        var snapshot = await GetSnapshotAsync(contactVault: false, ct).ConfigureAwait(false);
        var files = snapshot.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var all = moves.Select(m => new FileMove(ToRelativePath(m.From), ToRelativePath(m.To)))
            .Where(m => !string.Equals(m.From, m.To, StringComparison.Ordinal))
            .ToList();

        foreach (var move in all)
        {
            var name = PathList.Name(move.From);
            if (!files.TryGetValue(move.From, out var file) || !IsOnThisPc(file))
            {
                plan.Problems.Add($"{name} isn't on this PC. Download it first.");
                continue;
            }
            var problem = file.State switch
            {
                FileState.CheckedOutByOther => $"{name} is checked out by {file.CheckedOutBy}.",
                FileState.Frozen => $"{name} is frozen. An admin has to unfreeze it first.",
                FileState.InReview => $"{name} is in review.",
                FileState.ModifiedWithoutCheckOut or FileState.Modified => $"{name} has changes that aren't checked in. Check it in (or undo them) first.",
                FileState.CheckedOutByMe when file.HasLocalChanges => $"{name} has changes that aren't checked in. Check it in (or undo them) first.",
                _ when file.PinnedVersion != null => $"{name} is set to an older version on this PC. Return it to the latest version first.",
                _ => null,
            };
            if (problem != null)
            {
                plan.Problems.Add(problem);
                continue;
            }
            var destination = ToFullPath(move.To);
            var sameFileOtherCase = string.Equals(move.From, move.To, StringComparison.OrdinalIgnoreCase);
            if (!sameFileOtherCase && (File.Exists(destination) || files.ContainsKey(move.To)))
            {
                plan.Problems.Add($"There's already a file named {PathList.Name(move.To)} in {FolderText(move.To)}.");
                continue;
            }
            if (all.Count(m => string.Equals(m.To, move.To, StringComparison.OrdinalIgnoreCase)) > 1)
            {
                plan.Problems.Add($"Two of the files would both become {move.To}.");
                continue;
            }
            plan.Moves.Add(move);
        }
        if (plan.Problems.Count > 0 || plan.Moves.Count == 0) return plan;

        // Who uses the moved SolidWorks files?
        var movedSolidWorks = plan.Moves.Where(m => IsSolidWorksFile(m.From)).ToList();
        if (movedSolidWorks.Count == 0) return plan;
        if (editor == null)
        {
            plan.Problems.Add("Moving SolidWorks files needs SolidWorks, to update the assemblies and drawings that use them.");
            return plan;
        }

        var movedFull = new HashSet<string>(movedSolidWorks.Select(m => ToFullPath(m.From)), StringComparer.OrdinalIgnoreCase);
        var candidates = snapshot.Files.Where(f => IsOnThisPc(f) && IsSolidWorksFile(f.RelativePath)).ToList();
        var parents = new List<string>();
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 5 == 0) progress?.Report($"Finding files that use {(movedSolidWorks.Count == 1 ? PathList.Name(movedSolidWorks[0].From) : "them")} ({i + 1} of {candidates.Count})\u2026");
            var full = ToFullPath(candidates[i].RelativePath);
            if (movedFull.Contains(full) && !HasOwnReferences(full)) continue;
            if (editor.GetReferences(full).Any(r => movedFull.Contains(Path.GetFullPath(r)) && !string.Equals(Path.GetFullPath(r), full, StringComparison.OrdinalIgnoreCase)))
            {
                parents.Add(candidates[i].RelativePath);
            }
        }

        // Files that aren't downloaded here can't be read, but the index knows what they use.
        var index = VaultIndex.ReadAll(VaultRoot);
        foreach (var pair in VaultIndex.WhereUsed(index).Where(p => movedSolidWorks.Any(m => string.Equals(m.From, p.Key, StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var user in pair.Value.Where(u => files.TryGetValue(u, out var f) && !IsOnThisPc(f)))
            {
                plan.Problems.Add($"{PathList.Name(user)} uses {PathList.Name(pair.Key)} but isn't on this PC. Download it first, so its reference can be updated.");
            }
        }
        // An assembly or drawing that isn't here and was never indexed might use it too: nobody can tell, so don't risk breaking it.
        foreach (var unknown in snapshot.Files.Where(f => !IsOnThisPc(f) && HasOwnReferences(f.RelativePath) && !index.ContainsKey(f.RelativePath)))
        {
            plan.Problems.Add($"{unknown.Name} isn't on this PC and Jarvis doesn't know which files it uses, so it might use this one. " +
                              "Download it first (right-click it in Jarvis, Download), or have someone who has it check in any file from SolidWorks.");
        }

        foreach (var parent in parents.Where(p => !plan.Moves.Any(m => string.Equals(m.From, p, StringComparison.OrdinalIgnoreCase))))
        {
            var file = files[parent];
            var name = PathList.Name(parent);
            var problem = file.State switch
            {
                FileState.CheckedOutByOther => $"{name} uses it but is checked out by {file.CheckedOutBy}.",
                FileState.Frozen => $"{name} uses it but is frozen (an admin must unfreeze it).",
                FileState.InReview => $"{name} uses it but is in review.",
                FileState.ModifiedWithoutCheckOut or FileState.Modified => $"{name} uses it and has changes that aren't checked in. Check it in (or undo them) first.",
                FileState.CheckedOutByMe when file.HasLocalChanges => $"{name} uses it and has changes that aren't checked in. Check it in first.",
                FileState.NotInVault => $"{name} uses it but has never been checked in. Check it in first.",
                _ => null,
            };
            if (problem != null) plan.Problems.Add(problem);
            else plan.Parents.Add(parent);
        }
        // A moved file that uses another moved file is updated too, at its new place.
        foreach (var parent in parents.Where(p => plan.Moves.Any(m => string.Equals(m.From, p, StringComparison.OrdinalIgnoreCase))))
        {
            plan.Parents.Add(parent);
        }
        if (plan.Parents.Count > 0)
        {
            foreach (var move in plan.Moves.Where(m => files[m.From].State == FileState.NotInVault))
            {
                plan.Problems.Add($"{PathList.Name(move.From)} has never been checked in, and other files use it. Check it in first, then move it.");
            }
        }
        return plan;
    }

    /// <summary>The real file is here (not missing, and not just the placeholder "only download my folders" leaves).</summary>
    private static bool IsOnThisPc(VaultFile file) => file.ExistsLocally && !file.NotDownloaded;

    private static bool HasOwnReferences(string fullPath) =>
        fullPath.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) || fullPath.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase);

    private static string FolderText(string relativePath)
    {
        var i = relativePath.LastIndexOf('/');
        return i < 0 ? "the top of the vault" : relativePath.Substring(0, i);
    }

    /// <summary>
    /// Moves (or renames) files as planned and checks the result in, in one change: the files at their new
    /// places, the old places removed, and every file that uses them repointed. If anything fails before
    /// the check-in, everything is put back.
    /// </summary>
    public async Task<OperationResult> MoveAsync(MovePlan plan, IReferenceEditor? editor, string comment, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!plan.CanMove) throw new PdmException(plan.Problems.Count > 0 ? string.Join("\n", plan.Problems) : "Nothing to move.");
        if (plan.Parents.Count > 0 && editor == null) throw new PdmException("Moving SolidWorks files needs SolidWorks.");

        var snapshot = await GetSnapshotAsync(contactVault: false, ct).ConfigureAwait(false);
        var files = snapshot.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var newPlace = plan.Moves.ToDictionary(m => m.From, m => m.To, StringComparer.OrdinalIgnoreCase);
        string Now(string rel) => newPlace.TryGetValue(rel, out var to) ? to : rel;

        // 1. Check out what's in the vault and lockable.
        var toCheckOut = plan.Moves.Select(m => m.From).Concat(plan.Parents).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => files.TryGetValue(r, out var f) && f.IsLockable && f.State == FileState.Available)
            .ToList();
        if (toCheckOut.Count > 0)
        {
            var checkOut = await CheckOutAsync(toCheckOut, progress, ct).ConfigureAwait(false);
            if (checkOut.Failures.Count > 0) throw checkOut.Failures[0].Error;
        }

        var involvedFull = plan.Moves.Select(m => ToFullPath(m.From)).Concat(plan.Parents.Select(ToFullPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var movedFull = plan.Moves.ToDictionary(m => ToFullPath(m.From), m => ToFullPath(m.To), StringComparer.OrdinalIgnoreCase);
        editor?.Release(involvedFull);

        var done = new List<FileMove>();
        var repointed = new List<(string File, string Old, string New)>();
        try
        {
            try
            {
                // 2. Move the files (a running SolidWorks file can't be moved; Release made sure nothing is open).
                progress?.Report("Moving\u2026");
                foreach (var move in plan.Moves)
                {
                    var from = ToFullPath(move.From);
                    var to = ToFullPath(move.To);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to);
                    done.Add(move);
                }

                // 3. Repoint every file that uses a moved one.
                if (plan.Parents.Count > 0) progress?.Report("Updating the assemblies and drawings that use it\u2026");
                foreach (var parent in plan.Parents)
                {
                    var parentFull = ToFullPath(Now(parent));
                    var references = editor!.GetReferences(parentFull).Select(Path.GetFullPath).ToList();
                    foreach (var pair in movedFull.Where(p => references.Contains(p.Key, StringComparer.OrdinalIgnoreCase)))
                    {
                        if (!editor.ReplaceReference(parentFull, pair.Key, pair.Value))
                        {
                            throw new PdmException($"SolidWorks couldn't update the reference to {Path.GetFileName(pair.Key)} in {Path.GetFileName(parentFull)}. Nothing was moved.");
                        }
                        repointed.Add((parentFull, pair.Key, pair.Value));
                    }
                }
            }
            catch
            {
                // Put everything back the way it was.
                foreach (var (file, oldReference, newReference) in Enumerable.Reverse(repointed)) editor!.ReplaceReference(file, newReference, oldReference);
                foreach (var move in Enumerable.Reverse(done))
                {
                    var to = ToFullPath(move.To);
                    if (File.Exists(to) && !File.Exists(ToFullPath(move.From))) File.Move(to, ToFullPath(move.From));
                }
                throw;
            }

            // 4. The index entries and 3D previews move with their files; the users' entries point at the new paths.
            var indexUpdates = new Dictionary<string, IndexEntry?>(StringComparer.OrdinalIgnoreCase);
            var previews = new List<string>();
            IndexEntry? Repointed(IndexEntry? entry)
            {
                if (entry == null) return null;
                entry.References = entry.References.Select(Now).ToList();
                return entry;
            }
            foreach (var move in plan.Moves)
            {
                if (files.TryGetValue(move.From, out var file) && file.Index != null)
                {
                    indexUpdates[move.From] = null;
                    indexUpdates[move.To] = Repointed(VaultIndex.Parse(VaultIndex.Serialize(file.Index)));
                }
                var oldPreview = ToFullPath(VaultPaths.PreviewFor(move.From));
                var newPreview = ToFullPath(VaultPaths.PreviewFor(move.To));
                if (File.Exists(oldPreview))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newPreview)!);
                    File.Move(oldPreview, newPreview);
                    previews.Add(VaultPaths.PreviewFor(move.From));
                    previews.Add(VaultPaths.PreviewFor(move.To));
                }
            }
            foreach (var parent in plan.Parents.Where(p => !newPlace.ContainsKey(p)))
            {
                if (files.TryGetValue(parent, out var file) && file.Index != null)
                {
                    indexUpdates[parent] = Repointed(VaultIndex.Parse(VaultIndex.Serialize(file.Index)));
                }
            }

            // 5. One check-in: old places removed, new places added, users updated. Files that were never
            //    checked in are only moved on this PC (checking them in is the user's call).
            var localOnly = new HashSet<string>(plan.Moves.Where(m => files.TryGetValue(m.From, out var f) && f.State == FileState.NotInVault)
                .SelectMany(m => new[] { m.From, m.To }), StringComparer.OrdinalIgnoreCase);
            var paths = plan.Moves.SelectMany(m => new[] { m.From, m.To }).Concat(plan.Parents.Select(Now))
                .Where(p => !localOnly.Contains(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0)
            {
                var local = new OperationResult();
                local.Succeeded.AddRange(plan.Moves.Select(m => m.To));
                local.Notes.Add("These files have never been checked in, so they were only moved on this PC.");
                return local;
            }
            OperationResult result;
            try
            {
                result = await CheckInAsync(paths, comment, progress: progress, ct: ct, indexUpdates: indexUpdates, previews: previews).ConfigureAwait(false);
                result.Succeeded.RemoveAll(p => newPlace.ContainsKey(p));
            }
            catch (PdmException ex)
            {
                throw new PdmException(ex.Message + "\n\nThe files are moved on this PC but the move isn't in the vault yet. " +
                                       "Select the moved files and the files that use them, and click Check In to finish.", ex.Detail, ex);
            }

            // 6. What was checked out to you before the move stays checked out (at its new place), so you can keep working.
            var keep = plan.Moves.Select(m => m.From).Concat(plan.Parents)
                .Where(r => files.TryGetValue(r, out var f) && f.IsLockable && f.State == FileState.CheckedOutByMe)
                .Select(Now).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (keep.Count > 0)
            {
                var again = await CheckOutAsync(keep, progress, ct).ConfigureAwait(false);
                result.Warnings.AddRange(again.Failures.Select(f => $"{PathList.Name(f.Path)} was moved but couldn't be checked out to you again: {f.Error.Message} Check it out before editing."));
            }
            return result;
        }
        finally
        {
            editor?.Restore(done.ToDictionary(m => ToFullPath(m.From), m => ToFullPath(m.To), StringComparer.OrdinalIgnoreCase));
        }
    }

    // ---------------------------------------------------------------- folders

    /// <summary>
    /// Every folder in the vault (vault-relative, "/" separated, sorted): the folders files are in, plus empty
    /// ones made on this PC. Leaves out Git's and Jarvis's own (.git, .pdm, _versions) and other hidden ones.
    /// </summary>
    public IReadOnlyList<string> ListFolders(IEnumerable<string> fileRelativePaths)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddWithParents(string folder)
        {
            while (folder.Length > 0 && folders.Add(folder))
            {
                var i = folder.LastIndexOf('/');
                folder = i < 0 ? "" : folder.Substring(0, i);
            }
        }
        foreach (var path in fileRelativePaths)
        {
            var i = path.LastIndexOf('/');
            if (i > 0) AddWithParents(path.Substring(0, i));
        }
        void Walk(string full, string relative)
        {
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(full).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return;
            }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                var childRelative = relative.Length == 0 ? name : relative + "/" + name;
                folders.Add(childRelative);
                Walk(child, childRelative);
            }
        }
        Walk(VaultRoot, "");
        // Hidden folders and the read-only older-version copies ("_versions") aren't places to put files.
        return folders.Where(f => !f.Split('/').Any(part => part.StartsWith(".", StringComparison.Ordinal) ||
                                                            string.Equals(part, VersionCopies.FolderName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Checks a new folder name; returns what's wrong with it, or null if it's fine.</summary>
    public static string? FolderNameProblem(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "Type a name for the folder.";
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            return "A folder name can't contain any of these: \\ / : * ? \" < > |";
        }
        if (trimmed.StartsWith(".", StringComparison.Ordinal) || trimmed.EndsWith(".", StringComparison.Ordinal)) return "A folder name can't start or end with a dot.";
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
        if (reserved.Contains(trimmed.ToUpperInvariant())) return $"Windows doesn't allow a folder named {trimmed}.";
        return null;
    }

    /// <summary>
    /// Makes a folder inside <paramref name="parentFolder"/> ("" is the top of the vault) and returns its
    /// vault-relative path. It's only on this PC until files are moved into it: Git doesn't keep empty folders.
    /// </summary>
    public string CreateFolder(string parentFolder, string name)
    {
        var problem = FolderNameProblem(name);
        if (problem != null) throw new PdmException(problem);
        var parent = parentFolder.Replace('\\', '/').Trim('/');
        var relative = parent.Length == 0 ? name.Trim() : parent + "/" + name.Trim();
        var full = ToFullPath(relative);
        if (Directory.Exists(full) || File.Exists(full)) throw new PdmException($"There's already a folder named {name.Trim()} in {(parent.Length == 0 ? "the top of the vault" : parent)}.");
        Directory.CreateDirectory(full);
        return relative;
    }

    /// <summary>Removes a folder that has nothing in it (vault files are moved or checked in away first).</summary>
    public void DeleteEmptyFolder(string folder)
    {
        var relative = folder.Replace('\\', '/').Trim('/');
        if (relative.Length == 0) throw new PdmException("The top of the vault can't be deleted.");
        var full = ToFullPath(relative);
        if (!Directory.Exists(full)) return;
        if (Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories).Any(e => File.Exists(e)))
        {
            throw new PdmException($"{relative} isn't empty. Move its files to another folder first.");
        }
        Directory.Delete(full, recursive: true); // only empty subfolders are left
    }

    /// <summary>"Moved bracket.SLDPRT to Frame" or "Renamed a to b": the check-in comment for a move.</summary>
    public static string MoveComment(IReadOnlyList<FileMove> moves)
    {
        if (moves.Count == 1)
        {
            var move = moves[0];
            var sameFolder = string.Equals(FolderText(move.From), FolderText(move.To), StringComparison.OrdinalIgnoreCase);
            return sameFolder ? $"Renamed {move.From} to {PathList.Name(move.To)}" : $"Moved {PathList.Name(move.From)} to {FolderText(move.To)}";
        }
        var folders = moves.Select(m => FolderText(m.To)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return folders.Count == 1 ? $"Moved {moves.Count} files to {folders[0]}" : $"Moved {moves.Count} files";
    }
}
