using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Jarvis.Core.Vault;

namespace Jarvis.SwAddin;

/// <summary>
/// Moving files from the SolidWorks pane (Rename / Move): Core's move logic, with this SolidWorks reading
/// and rewriting references. Core runs its steps on worker threads; every SolidWorks call is sent back to
/// the pane's (SolidWorks') UI thread, as the API requires.
/// </summary>
internal sealed class AddinReferenceEditor : IReferenceEditor
{
    private readonly SolidWorksSession _sw;
    private readonly Control _ui;
    private ClosedDocuments? _closed;

    public AddinReferenceEditor(SolidWorksSession sw, Control ui)
    {
        _sw = sw;
        _ui = ui;
    }

    public IReadOnlyList<string> GetReferences(string fullPath) =>
        OnUi(() => _sw.GetReferences(fullPath, allLevels: false, includeParts: true));

    public bool ReplaceReference(string referencingFile, string oldReference, string newReference) =>
        OnUi(() => _sw.ReplaceReference(referencingFile, oldReference, newReference));

    /// <summary>
    /// Closes the files and everything using them, so they can be moved and repointed. Documents you have
    /// checked out are saved first; read-only ones are closed without saving (they can't be saved, and an
    /// assembly often shows as changed just from being opened).
    /// </summary>
    public void Release(IReadOnlyList<string> fullPaths) =>
        OnUi(() => _closed = _sw.CloseDocumentsUsing(fullPaths, CloseMode.SaveWritableDiscardReadOnly));

    /// <summary>Reopens what was closed, at the new paths.</summary>
    public void Restore(IReadOnlyDictionary<string, string> movedFullPaths)
    {
        if (_closed == null) return;
        string Now(string? path) => path != null && movedFullPaths.TryGetValue(path, out var moved) ? moved : path ?? "";
        var reopen = new ClosedDocuments(_closed.ActivePath == null ? null : Now(_closed.ActivePath));
        reopen.Paths.AddRange(_closed.Paths.Select(p => Now(p)));
        _closed = null;
        OnUi(() =>
        {
            _sw.Reopen(reopen);
            return true;
        });
    }

    private T OnUi<T>(Func<T> call) => _ui.InvokeRequired ? (T)_ui.Invoke(call) : call();
}
