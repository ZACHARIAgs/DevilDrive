using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Core.Vault;

/// <summary>The vault's part-numbering convention (see <see cref="NumberingScheme"/>).</summary>
public sealed partial class VaultService
{
    /// <summary>The convention as of the last Get Latest, or null if the vault has none.</summary>
    public NumberingScheme? LoadNumbering()
    {
        var path = ToFullPath(NumberingScheme.RelativePath);
        try
        {
            return File.Exists(path) ? NumberingScheme.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Admin: saves the convention for everyone (null removes it). Checked in on its own.</summary>
    public async Task<OperationResult> SaveNumberingAsync(NumberingScheme? scheme, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        RequireAdmin("change the part numbering");
        if (scheme?.Problem() is { } problem) throw new Errors.PdmException(problem);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SyncCoreAsync(progress, ct).ConfigureAwait(false);
            var full = ToFullPath(NumberingScheme.RelativePath);
            if (scheme == null)
            {
                if (File.Exists(full)) File.Delete(full);
            }
            else
            {
                scheme.Groups = scheme.Groups.OrderBy(g => g.Number).ToList();
                WriteVaultText(NumberingScheme.RelativePath, scheme.Serialize());
            }
            await CommitAndPushAsync(new[] { NumberingScheme.RelativePath }, scheme == null ? "Removed the part numbering" : "Updated the part numbering", progress, ct).ConfigureAwait(false);
            var result = new OperationResult();
            result.Succeeded.Add(NumberingScheme.RelativePath);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Part numbers already taken: file names and PartNumber properties of every file in the vault.</summary>
    public static IReadOnlyList<string> UsedPartNumbers(IEnumerable<VaultFile> files) =>
        files.SelectMany(f => new[] { f.Name, f.Index?.GetProperty(IndexEntry.PartNumberNames) ?? "" })
            .Where(s => s.Length > 0)
            .ToList();
}
