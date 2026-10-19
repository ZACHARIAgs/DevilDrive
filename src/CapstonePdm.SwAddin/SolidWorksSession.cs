using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CapstonePdm.Core.Errors;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CapstonePdm.SwAddin;

/// <summary>What to do with unsaved changes when documents must be closed.</summary>
internal enum CloseMode
{
    /// <summary>Stop if anything has unsaved changes.</summary>
    RequireSaved,
    /// <summary>Save writable documents first (Check In).</summary>
    Save,
    /// <summary>Throw away unsaved changes in the target files only (Undo Check Out).</summary>
    DiscardTargets,
}

internal sealed class ClosedDocuments
{
    public ClosedDocuments(string? activePath)
    {
        ActivePath = activePath;
    }

    public string? ActivePath { get; }
    public List<string> Paths { get; } = new List<string>();
}

/// <summary>
/// Thin wrapper over the SolidWorks API: open documents, references, and closing/reopening
/// files so Git can replace them (SolidWorks keeps a handle on every open file).
/// Uses only calls available in SolidWorks 2024.
/// </summary>
internal sealed class SolidWorksSession
{
    private readonly ISldWorks _sw;

    public SolidWorksSession(ISldWorks sw)
    {
        _sw = sw;
    }

    public IWin32Window MainWindow
    {
        get
        {
            try
            {
                return new Win32Window(new IntPtr(((IFrame)(object)_sw.Frame()).GetHWndx64()));
            }
            catch (Exception)
            {
                return new Win32Window(IntPtr.Zero);
            }
        }
    }

    public IModelDoc2? ActiveDocument => _sw.ActiveDoc as IModelDoc2;

    /// <summary>Full path of the active document, or null if none is open or it was never saved.</summary>
    public string? ActiveDocumentPath
    {
        get
        {
            var path = ActiveDocument?.GetPathName();
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    public IModelDoc2? FindOpen(string path) => _sw.GetOpenDocumentByName(path) as IModelDoc2;

    public static bool HasReferences(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".sldasm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".slddrw", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every file this document references, all levels down (parts in an assembly, models in a drawing).</summary>
    public List<string> GetReferences(string path)
    {
        var result = new List<string>();
        if (!HasReferences(path)) return result;

        // Returns pairs: file name, full path.
        if (_sw.GetDocumentDependencies2(path, true, true, false) is Array pairs)
        {
            for (var i = 1; i < pairs.Length; i += 2)
            {
                var reference = pairs.GetValue(i)?.ToString();
                if (!string.IsNullOrEmpty(reference) && !reference!.Equals(path, StringComparison.OrdinalIgnoreCase)) result.Add(reference);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Documents open in their own window (not parts loaded only as assembly components).</summary>
    public List<IModelDoc2> VisibleDocuments()
    {
        var list = new List<IModelDoc2>();
        var doc = _sw.GetFirstDocument() as IModelDoc2;
        while (doc != null)
        {
            if (doc.Visible && !string.IsNullOrEmpty(doc.GetPathName())) list.Add(doc);
            doc = doc.GetNext() as IModelDoc2;
        }
        return list;
    }

    /// <summary>Lets SolidWorks edit files that were opened read-only before they were checked out.</summary>
    public void MakeWritable(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var doc = FindOpen(path);
            if (doc != null && doc.IsOpenedReadOnly()) doc.SetReadOnlyState(false);
        }
    }

    /// <summary>
    /// Closes the windows of these exact files when they have no unsaved changes, so Git can read and
    /// replace them. Files with unsaved changes stay open; their saved copy on disk is what gets used.
    /// </summary>
    public ClosedDocuments CloseIfSaved(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var closed = new ClosedDocuments(ActiveDocumentPath);
        foreach (var doc in VisibleDocuments())
        {
            var path = doc.GetPathName();
            if (!set.Contains(path) || doc.GetSaveFlag()) continue;
            _sw.CloseDoc(path);
            closed.Paths.Add(path);
        }
        return closed;
    }

    /// <summary>Switches open, unmodified documents to read-only (after freezing).</summary>
    public void MakeReadOnly(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var doc = FindOpen(path);
            if (doc != null && !doc.IsOpenedReadOnly() && !doc.GetSaveFlag()) doc.SetReadOnlyState(true);
        }
    }

    /// <summary>
    /// Closes every window that shows one of the targets or uses one of them, so their files are
    /// released. Returns what was closed so it can be reopened afterwards.
    /// </summary>
    public ClosedDocuments CloseDocumentsUsing(IEnumerable<string> targets, CloseMode mode)
    {
        var targetSet = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        var toClose = VisibleDocuments()
            .Where(doc =>
            {
                var path = doc.GetPathName();
                return targetSet.Contains(path) || GetReferences(path).Any(targetSet.Contains);
            })
            .ToList();

        foreach (var doc in toClose.Where(d => d.GetSaveFlag()))
        {
            var path = doc.GetPathName();
            if (mode == CloseMode.Save && !doc.IsOpenedReadOnly())
            {
                Save(doc);
                continue;
            }
            if (mode == CloseMode.DiscardTargets && targetSet.Contains(path)) continue;
            throw new PdmException($"{Path.GetFileName(path)} has unsaved changes. Save it (or close it without saving), then try again.");
        }

        var closed = new ClosedDocuments(ActiveDocumentPath);
        foreach (var doc in toClose)
        {
            var path = doc.GetPathName();
            var title = doc.GetTitle();
            _sw.CloseDoc(path);
            if (FindOpen(path) is IModelDoc2 stillOpen && stillOpen.Visible) _sw.CloseDoc(title);
            closed.Paths.Add(path);
        }
        return closed;
    }

    /// <summary>Reopens closed documents, the previously active one last so it ends up on top.</summary>
    public void Reopen(ClosedDocuments closed)
    {
        var ordered = closed.Paths
            .OrderBy(p => string.Equals(p, closed.ActivePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var path in ordered) Open(path);
    }

    public void Open(string path)
    {
        var type = DocumentType(path);
        if (type == swDocumentTypes_e.swDocNONE || !File.Exists(path)) return;

        int errors = 0, warnings = 0;
        _sw.OpenDoc6(path, (int)type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
        if (errors != 0) AddinLog.Write($"Reopening {path} reported error {errors}, warnings {warnings}");
    }

    private static void Save(IModelDoc2 doc)
    {
        int errors = 0, warnings = 0;
        var options = (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced);
        if (!doc.Save3(options, ref errors, ref warnings))
        {
            throw new PdmException($"SolidWorks couldn't save {Path.GetFileName(doc.GetPathName())} (error {errors}). Save it yourself, then try again.");
        }
    }

    private static swDocumentTypes_e DocumentType(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".sldprt": return swDocumentTypes_e.swDocPART;
            case ".sldasm": return swDocumentTypes_e.swDocASSEMBLY;
            case ".slddrw": return swDocumentTypes_e.swDocDRAWING;
            default: return swDocumentTypes_e.swDocNONE;
        }
    }

    private sealed class Win32Window : IWin32Window
    {
        public Win32Window(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }
}
