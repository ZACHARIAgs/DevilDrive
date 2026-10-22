using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>SolidWorks' COM name: "SldWorks.Application.34" is 2026 (revision = year - 1992).</summary>
    public static string ProgIdFor(int? year) => year is int y ? $"SldWorks.Application.{y - 1992}" : "SldWorks.Application";

    /// <summary>Connects to (or starts) the team's SolidWorks. Throws a <see cref="PdmException"/> saying what to do if it can't.</summary>
    public static SolidWorksAutomation Connect(int? teamYear, IProgress<string>? progress)
    {
        var type = Type.GetTypeFromProgID(ProgIdFor(teamYear));
        if (type == null)
        {
            throw new PdmException(teamYear != null
                ? $"Moving SolidWorks files needs SolidWorks {teamYear} (your team's version) on this PC, to update the assemblies and drawings that use them. " +
                  "Move them on a PC that has it, or with Rename / Move in SolidWorks' Jarvis pane."
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
            throw new PdmException("SolidWorks didn't start, so the files can't be moved from Jarvis. Use Rename / Move in SolidWorks' Jarvis pane instead.", ex.ToString());
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

    public IReadOnlyList<string> GetReferences(string fullPath)
    {
        var result = new List<string>();
        // Returns pairs: file name, full path.
        if (Call("GetDocumentDependencies2", fullPath, false, true, false) is Array pairs)
        {
            for (var i = 1; i < pairs.Length; i += 2)
            {
                var reference = pairs.GetValue(i)?.ToString();
                if (!string.IsNullOrEmpty(reference) && !reference!.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) result.Add(reference);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
