using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CapstonePdm.Core.Errors;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CapstonePdm.SwAddin;

/// <summary>
/// Pressing Save while editing a checked-out part inside an assembly that isn't checked out would
/// make SolidWorks save the (read-only) assembly, which turns into "Save As a new file". This catches
/// that save, saves just the checked-out parts that changed, and leaves the assembly alone.
/// </summary>
internal sealed class AssemblySaveGuard
{
    private readonly SolidWorksSession _sw;
    private readonly Dictionary<string, DAssemblyDocEvents_Event> _hooked = new Dictionary<string, DAssemblyDocEvents_Event>(StringComparer.OrdinalIgnoreCase);

    public AssemblySaveGuard(SolidWorksSession sw)
    {
        _sw = sw;
    }

    /// <summary>Raised after parts were saved this way (for the pane's status line).</summary>
    public event Action<string>? Saved;

    /// <summary>Starts watching an open assembly's Save (once per document).</summary>
    public void Watch(IModelDoc2 doc)
    {
        var path = doc.GetPathName();
        if (string.IsNullOrEmpty(path) || _hooked.ContainsKey(path) || !(doc is DAssemblyDocEvents_Event events)) return;
        events.FileSaveNotify += name => OnSave(name, saveAs: false);
        events.FileSaveAsNotify2 += name => OnSave(path, saveAs: true);
        events.DestroyNotify2 += _ => { _hooked.Remove(path); return 0; };
        _hooked[path] = events;
    }

    /// <returns>1 cancels the assembly save, 0 lets SolidWorks carry on.</returns>
    private int OnSave(string assemblyPath, bool saveAs)
    {
        try
        {
            var assembly = _sw.FindOpen(assemblyPath);
            if (assembly == null || !assembly.IsOpenedReadOnly()) return 0; // checked out: a normal save
            var saved = SaveWritableComponents(assembly);
            var name = Path.GetFileName(assemblyPath);
            if (saveAs)
            {
                if (saved.Count == 0) return 0; // a deliberate Save As of a read-only assembly
                var answer = MessageBox.Show(_sw.MainWindow,
                    $"Saved {PathList.Format(saved.Select(Path.GetFileName))}.\n\n{name} isn't checked out, so it can't be saved over. " +
                    "Do you also want to save the assembly as a NEW file?",
                    "Jarvis", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                Saved?.Invoke($"Saved {PathList.Format(saved.Select(Path.GetFileName))}.");
                return answer == DialogResult.Yes ? 0 : 1;
            }

            Saved?.Invoke(saved.Count > 0
                ? $"Saved {PathList.Format(saved.Select(Path.GetFileName))}. {name} isn't checked out, so it wasn't saved."
                : $"{name} isn't checked out, so it wasn't saved. Check it out to save changes to the assembly.");
            return 1;
        }
        catch (Exception ex)
        {
            AddinLog.Write("Save guard failed: " + ex);
            return 0;
        }
    }

    /// <summary>Saves the component files in the assembly that are writable (checked out) and have unsaved changes.</summary>
    private static List<string> SaveWritableComponents(IModelDoc2 assembly)
    {
        var saved = new List<string>();
        if (!(assembly is IAssemblyDoc asm) || !(asm.GetComponents(false) is Array components)) return saved;
        var docs = components.OfType<IComponent2>()
            .Select(c => c.GetModelDoc2() as IModelDoc2)
            .Where(d => d != null)
            .GroupBy(d => d!.GetPathName(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()!);
        foreach (var doc in docs)
        {
            if (!doc.GetSaveFlag() || doc.IsOpenedReadOnly()) continue;
            int errors = 0, warnings = 0;
            if (doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings)) saved.Add(doc.GetPathName());
            else AddinLog.Write($"Couldn't save {doc.GetPathName()} (error {errors}).");
        }
        return saved;
    }
}
