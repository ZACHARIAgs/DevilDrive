using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;

namespace Jarvis.Client.Services;

/// <summary>
/// Lets the desktop app move SolidWorks files: drives SolidWorks through COM to read and rewrite the
/// references in assemblies and drawings. Uses the team's SolidWorks version, so files are never touched
/// by a newer one. Attaches to that SolidWorks if it's running; otherwise starts it hidden and closes it
/// again when done. Create and use it off the UI thread: starting SolidWorks takes a while.
/// </summary>
/// <remarks>
/// Calls go through IDispatch by name (Type.InvokeMember). C# "dynamic" and PowerShell need SolidWorks'
/// type information, which isn't available when two SolidWorks versions are installed side by side.
/// </remarks>
internal sealed class SolidWorksAutomation : IReferenceEditor, IDisposable
{
    private object? _sw;
    private readonly IReadOnlyList<int> _startedProcesses;

    private SolidWorksAutomation(object sw, IReadOnlyList<int> startedProcesses)
    {
        _sw = sw;
        _startedProcesses = startedProcesses;
    }

    /// <summary>Jarvis started this SolidWorks (hidden); it's closed again when this is disposed.</summary>
    public bool StartedHere => _startedProcesses.Count > 0;

    /// <summary>SolidWorks' COM name: "SldWorks.Application.34" is 2026 (revision = year - 1992).</summary>
    public static string ProgIdFor(int? year) => year is int y ? $"SldWorks.Application.{y - 1992}" : "SldWorks.Application";

    /// <summary>Connects to (or starts) the team's SolidWorks. Throws a <see cref="PdmException"/> saying what to do if it can't.</summary>
    /// <param name="purpose">What it's for, for the message when SolidWorks isn't there (e.g. "Exporting SolidWorks files"); moving by default.</param>
    public static SolidWorksAutomation Connect(int? teamYear, IProgress<string>? progress, string? purpose = null)
    {
        var type = Type.GetTypeFromProgID(ProgIdFor(teamYear));
        if (type == null && purpose != null)
        {
            throw new PdmException($"{purpose} needs SolidWorks{(teamYear != null ? $" {teamYear} (your team's version)" : "")} on this PC, to point the copied assemblies and drawings at each other.");
        }
        if (type == null)
        {
            throw new PdmException(teamYear != null
                ? $"Moving SolidWorks files needs SolidWorks {teamYear} (your team's version) on this PC, to update the assemblies and drawings that use them. " +
                  "Move them on a PC that has it, or with Rename / Move in SolidWorks' DevilDrive pane."
                : "Moving SolidWorks files needs SolidWorks on this PC, to update the assemblies and drawings that use them.");
        }

        var before = new HashSet<int>(Process.GetProcessesByName("SLDWORKS").Select(p => p.Id));
        if (before.Count == 0) progress?.Report("Starting SolidWorks in the background (this can take a minute)\u2026");
        object sw;
        try
        {
            sw = Activator.CreateInstance(type) ?? throw new COMException("No SolidWorks object.");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException)
        {
            throw new PdmException("SolidWorks didn't start, so the files can't be moved from DevilDrive. Use Rename / Move in SolidWorks' DevilDrive pane instead.", ex.ToString());
        }
        var started = Process.GetProcessesByName("SLDWORKS").Select(p => p.Id).Where(id => !before.Contains(id)).ToList();
        var automation = new SolidWorksAutomation(sw, started);
        if (started.Count > 0)
        {
            try
            {
                automation.Set("Visible", false);
                automation.Set("UserControl", false);
            }
            catch (Exception ex) when (ex is COMException or TargetInvocationException)
            {
                // Just cosmetic.
            }
        }
        return automation;
    }

    private object? Call(string method, params object[] args) =>
        _sw!.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, _sw, args);

    private void Set(string property, object value) =>
        _sw!.GetType().InvokeMember(property, BindingFlags.SetProperty, null, _sw, new[] { value });

    /// <summary>
    /// <see cref="GetReferences"/> gives the paths saved in the files instead of where SolidWorks finds them now. Export
    /// needs that: SolidWorks finds a copied part next to the copied assembly first, so the saved vault path, which is
    /// what gets rewritten, wouldn't show.
    /// </summary>
    public bool StoredPaths { get; set; }

    public IReadOnlyList<string> GetReferences(string fullPath)
    {
        var result = new List<string>();
        // Returns pairs: file name, full path.
        if (Call("GetDocumentDependencies2", fullPath, false, !StoredPaths, false) is Array pairs)
        {
            for (var i = 1; i < pairs.Length; i += 2)
            {
                var reference = pairs.GetValue(i)?.ToString();
                if (!string.IsNullOrEmpty(reference) && !reference!.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) result.Add(reference);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The Jarvis add-in's COM id: SolidWorks hands out the running add-in by it (GetAddInObject).</summary>
    private const string AddinClsid = "{843995B8-683B-4864-9733-A8E3A87F5ABC}";

    /// <summary>
    /// Asks the Jarvis add-in inside SolidWorks to make index records (properties, references, saved-in version;
    /// no 3D previews, see JarvisAddin.RecordFiles) for these files. Null if the add-in isn't loaded in this
    /// SolidWorks (or is older than 0.7.1); the files then get their records at a later check-in from SolidWorks.
    /// </summary>
    public IndexBatch? Record(string vaultRoot, IReadOnlyList<string> fullPaths)
    {
        try
        {
            var addin = Call("GetAddInObject", AddinClsid);
            if (addin == null) return null; // not loaded in this SolidWorks (one started in the background doesn't load add-ins)
            var json = addin.GetType().InvokeMember("RecordFiles", BindingFlags.InvokeMethod, null, addin, new object[] { vaultRoot, string.Join("\n", fullPaths) }) as string;
            return IndexBatch.Parse(json);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or MissingMethodException)
        {
            return null;
        }
    }

    public bool ReplaceReference(string referencingFile, string oldReference, string newReference) =>
        Call("ReplaceReferencedDocument", referencingFile, oldReference, newReference) is true;

    /// <summary>The app can't close the user's documents for them: refuse if any of the files are open.</summary>
    public void Release(IReadOnlyList<string> fullPaths)
    {
        var open = fullPaths.Where(p => IsOpenInSolidWorks(p) || FileUtil.IsInUse(p)).ToList();
        if (open.Count > 0) throw new FileInUseException(open, "Save and close them in SolidWorks, then move again.");
    }

    public void Restore(IReadOnlyDictionary<string, string> movedFullPaths)
    {
        // Nothing was closed, so there's nothing to reopen.
    }

    /// <summary>
    /// Writes file-level custom properties (e.g. Description) into a SolidWorks file: opens it hidden, sets them,
    /// saves and closes it. Refuses a file that's open in SolidWorks, which would save the user's unsaved changes too.
    /// </summary>
    public void SetProperties(string fullPath, IReadOnlyDictionary<string, string> properties)
    {
        if (IsOpenInSolidWorks(fullPath) || FileUtil.IsInUse(fullPath))
        {
            throw new FileInUseException(new[] { fullPath }, "Close it in SolidWorks, then try again.");
        }
        var type = Path.GetExtension(fullPath).ToUpperInvariant() switch { ".SLDPRT" => 1, ".SLDASM" => 2, ".SLDDRW" => 3, _ => 0 };
        if (type == 0) throw new PdmException($"{Path.GetFileName(fullPath)} isn't a SolidWorks file.");
        Call("DocumentVisible", false, type);
        object? doc;
        var openArgs = new object?[] { fullPath, type, 1 /* silent */, "", 0, 0 };
        try
        {
            doc = _sw!.GetType().InvokeMember("OpenDoc6", BindingFlags.InvokeMethod, null, _sw, openArgs, new[] { new ParameterModifier(6) { [4] = true, [5] = true } }, null, null);
        }
        finally
        {
            Call("DocumentVisible", true, type);
        }
        if (doc == null) throw new PdmException($"SolidWorks couldn't open {Path.GetFileName(fullPath)} (error {openArgs[4]}).");
        try
        {
            var extension = doc.GetType().InvokeMember("Extension", BindingFlags.GetProperty, null, doc, null)!;
            var manager = extension.GetType().InvokeMember("CustomPropertyManager", BindingFlags.GetProperty, null, extension, new object[] { "" })!;
            foreach (var property in properties)
            {
                manager.GetType().InvokeMember("Add3", BindingFlags.InvokeMethod, null, manager, new object[] { property.Key, 30 /* text */, property.Value, 2 /* replace the value */ });
            }
            var saveArgs = new object?[] { 1 /* silent */, 0, 0 };
            var saved = doc.GetType().InvokeMember("Save3", BindingFlags.InvokeMethod, null, doc, saveArgs, new[] { new ParameterModifier(3) { [1] = true, [2] = true } }, null, null);
            if (saved is not true) throw new PdmException($"SolidWorks couldn't save {Path.GetFileName(fullPath)} (error {saveArgs[1]}).");
        }
        finally
        {
            Call("CloseDoc", fullPath);
        }
    }

    private static object? Invoke(object target, string method, params object[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

    private static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);

    /// <summary>
    /// Flattened, parts-only bill of materials of an assembly, the same list the SolidWorks pane's Export BOM makes:
    /// every part at any level, counted per configuration, without suppressed, envelope and excluded-from-BOM
    /// components (and everything inside them). Opens the assembly hidden and read-only unless it's already open.
    /// Properties are left empty: the caller fills them from the vault's records.
    /// </summary>
    public List<BomLine> GetBom(string assemblyPath)
    {
        object? doc = null;
        try
        {
            doc = Call("GetOpenDocumentByName", assemblyPath);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException)
        {
            // Not open.
        }
        var openedHere = false;
        if (doc == null)
        {
            const int assembly = 2;
            // Silent, read-only and lightweight: only the structure is needed, not every part's geometry.
            var openArgs = new object?[] { assemblyPath, assembly, 1 | 2 | 64 | 128, "", 0, 0 };
            Call("DocumentVisible", false, assembly);
            try
            {
                doc = _sw!.GetType().InvokeMember("OpenDoc6", BindingFlags.InvokeMethod, null, _sw, openArgs, new[] { new ParameterModifier(6) { [4] = true, [5] = true } }, null, null);
            }
            finally
            {
                Call("DocumentVisible", true, assembly);
            }
            if (doc == null) throw new PdmException($"SolidWorks couldn't open {Path.GetFileName(assemblyPath)} (error {openArgs[4]}).");
            openedHere = true;
        }
        try
        {
            var lines = new Dictionary<string, BomLine>(StringComparer.OrdinalIgnoreCase);
            if (Invoke(doc, "GetComponents", false) is not Array components) return new List<BomLine>();
            foreach (var component in components)
            {
                if (component == null || Excluded(component)) continue;
                var path = Invoke(component, "GetPathName") as string ?? "";
                if (!Path.GetExtension(path).Equals(".sldprt", StringComparison.OrdinalIgnoreCase)) continue;
                var configuration = Get(component, "ReferencedConfiguration") as string ?? "";
                var key = path + "|" + configuration;
                if (!lines.TryGetValue(key, out var line)) lines[key] = line = new BomLine { Path = path, Configuration = configuration };
                line.Quantity++;
            }
            return lines.Values.ToList();
        }
        finally
        {
            if (openedHere) Call("CloseDoc", assemblyPath);
        }
    }

    /// <summary>Suppressed, an envelope or excluded from the BOM, itself or through any assembly it sits in.</summary>
    private static bool Excluded(object component)
    {
        for (var c = component; c != null; c = Invoke(c, "GetParent"))
        {
            if (Invoke(c, "IsSuppressed") is true || Get(c, "ExcludeFromBOM") is true || Invoke(c, "IsEnvelope") is true) return true;
        }
        return false;
    }

    private bool IsOpenInSolidWorks(string fullPath)
    {
        try
        {
            return Call("GetOpenDocumentByName", fullPath) != null;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException)
        {
            return false;
        }
    }

    /// <summary>Lets go of SolidWorks; closes it if Jarvis started it.</summary>
    public void Dispose()
    {
        if (_sw == null) return;
        var nothingOpen = false;
        try
        {
            if (_startedProcesses.Count > 0)
            {
                nothingOpen = Call("GetDocumentCount") is 0;
                Call("ExitApp");
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException)
        {
            // Already gone.
        }
        Marshal.FinalReleaseComObject(_sw);
        _sw = null;

        // A SolidWorks started through COM doesn't always quit on ExitApp. It's hidden and has nothing open, so end it.
        foreach (var id in _startedProcesses)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (!process.WaitForExit(20000) && nothingOpen) process.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
    }
}
