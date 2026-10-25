using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Jarvis.Core.Errors;
using Jarvis.Core.Vault;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Jarvis.SwAddin;

/// <summary>What to do with unsaved changes when documents must be closed.</summary>
internal enum CloseMode
{
    /// <summary>Stop if anything has unsaved changes.</summary>
    RequireSaved,
    /// <summary>Save writable documents first (Check In).</summary>
    Save,
    /// <summary>Throw away unsaved changes in the target files only (Undo Check Out).</summary>
    DiscardTargets,
    /// <summary>Close read-only (not checked out) documents even with unsaved changes: they can't be saved anyway (switching versions).</summary>
    DiscardReadOnly,
    /// <summary>
    /// Save writable documents and close read-only ones without saving (Rename / Move). Assemblies opened
    /// read-only often show as changed right after opening (rebuilt mates); that can't be saved and isn't real work.
    /// </summary>
    SaveWritableDiscardReadOnly,
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

    /// <summary>The running SolidWorks as a year, e.g. 2024 (revision 32.x).</summary>
    public int RunningYear
    {
        get
        {
            var revision = _sw.RevisionNumber() ?? "";
            var dot = revision.IndexOf('.');
            return int.TryParse(dot > 0 ? revision.Substring(0, dot) : revision, out var major) ? major + 1992 : 0;
        }
    }

    /// <summary>
    /// The SolidWorks version (year) that last saved the file, read without opening it. VersionHistory
    /// returns entries like "17000[2024/282]"; file version 17000 is SolidWorks 2024, 19000 is 2026.
    /// </summary>
    public int? SavedInYear(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (!(_sw.VersionHistory(path) is Array history) || history.Length == 0) return null;
            var last = history.GetValue(history.Length - 1)?.ToString() ?? "";
            var digits = new string(last.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var fileVersion) ? FileVersionToYear(fileVersion) : null;
        }
        catch (Exception ex)
        {
            AddinLog.Write($"Couldn't read the SolidWorks version of {path}: {ex.Message}");
            return null;
        }
    }

    public static int? FileVersionToYear(int fileVersion) => fileVersion >= 8000 ? fileVersion / 1000 + 2007 : null;

    public static bool HasReferences(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".sldasm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".slddrw", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Files this document references (parts in an assembly, models in a drawing, base parts of a
    /// derived part). All levels down by default; <paramref name="allLevels"/> false gives direct references only.
    /// </summary>
    public List<string> GetReferences(string path, bool allLevels = true, bool includeParts = false)
    {
        var result = new List<string>();
        if (!HasReferences(path) && !(includeParts && DocumentType(path) == swDocumentTypes_e.swDocPART)) return result;

        // Returns pairs: file name, full path.
        if (_sw.GetDocumentDependencies2(path, allLevels, true, false) is Array pairs)
        {
            for (var i = 1; i < pairs.Length; i += 2)
            {
                var reference = pairs.GetValue(i)?.ToString();
                if (!string.IsNullOrEmpty(reference) && !reference!.Equals(path, StringComparison.OrdinalIgnoreCase)) result.Add(reference);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// What an open document uses, for the "files in use" tree: an assembly's component tree (every
    /// instance, all levels; suppressed ones without their contents), or the models a drawing shows.
    /// A part uses nothing. Call on the main thread.
    /// </summary>
    public List<RawComponent> GetFilesInUse(IModelDoc2 doc)
    {
        var path = doc.GetPathName() ?? "";
        switch ((swDocumentTypes_e)doc.GetType())
        {
            case swDocumentTypes_e.swDocASSEMBLY:
                return AssemblyComponents(doc, path);
            case swDocumentTypes_e.swDocDRAWING:
                return path.Length == 0
                    ? new List<RawComponent>()
                    : GetReferences(path, allLevels: false).Select(r => new RawComponent { Path = r }).ToList();
            default:
                return new List<RawComponent>();
        }
    }

    /// <summary>
    /// An assembly's component instances. Tries the component tree first; if SolidWorks hands back
    /// nothing (components not loaded yet, lightweight or Large Design Review), falls back to the flat
    /// component list, then to the files the assembly references (read from the file, no loading needed).
    /// </summary>
    private List<RawComponent> AssemblyComponents(IModelDoc2 doc, string path)
    {
        var assembly = doc as IAssemblyDoc;
        var count = ComponentCount(doc);
        var root = doc.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true);
        var fromTree = root == null ? new List<RawComponent>() : ChildrenOf(root);
        var fromList = fromTree.Count == 0 && assembly != null ? FromComponentList(assembly) : new List<RawComponent>();
        var fromFile = fromTree.Count == 0 && fromList.Count == 0 && path.Length > 0
            ? GetReferences(path, allLevels: false).Select(r => new RawComponent { Path = r }).ToList()
            : new List<RawComponent>();
        AddinLog.Write($"Files in use for {Path.GetFileName(path)}: componentCount={count}, root={(root != null)}, " +
                       $"tree={fromTree.Count}, list={fromList.Count}, references={fromFile.Count}, viewOnly={SafeViewOnly(doc)}");
        return fromTree.Count > 0 ? fromTree : fromList.Count > 0 ? fromList : fromFile;
    }

    private static bool SafeViewOnly(IModelDoc2 doc)
    {
        try
        {
            return doc.IsOpenedViewOnly();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Builds the instance tree from IAssemblyDoc.GetComponents, using each component's parent.</summary>
    private static List<RawComponent> FromComponentList(IAssemblyDoc assembly)
    {
        if (!(assembly.GetComponents(false) is Array all)) return new List<RawComponent>();
        var byComponent = new Dictionary<string, (RawComponent Raw, string? Parent)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in all)
        {
            if (!(item is IComponent2 c)) continue;
            var name = c.Name2 ?? "";
            var raw = new RawComponent { Path = c.GetPathName() ?? "", InstanceName = name, Configuration = c.ReferencedConfiguration ?? "", IsSuppressed = c.IsSuppressed() };
            byComponent[name] = (raw, c.GetParent()?.Name2);
        }
        var top = new List<RawComponent>();
        foreach (var (raw, parent) in byComponent.Values)
        {
            if (parent != null && byComponent.TryGetValue(parent, out var p)) p.Raw.Children.Add(raw);
            else top.Add(raw);
        }
        return top;
    }

    /// <summary>All components at every level (0 for parts and drawings); cheap, used to notice loading and edits.</summary>
    public static int ComponentCount(IModelDoc2 doc)
    {
        try
        {
            return doc is IAssemblyDoc assembly ? assembly.GetComponentCount(false) : 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static List<RawComponent> ChildrenOf(IComponent2 parent)
    {
        var result = new List<RawComponent>();
        if (!(parent.GetChildren() is Array children)) return result;
        foreach (var item in children)
        {
            if (!(item is IComponent2 child)) continue;
            var raw = new RawComponent
            {
                Path = child.GetPathName() ?? "",
                InstanceName = child.Name2 ?? "",
                Configuration = child.ReferencedConfiguration ?? "",
                IsSuppressed = child.IsSuppressed(),
            };
            if (!raw.IsSuppressed) raw.Children = ChildrenOf(child);
            result.Add(raw);
        }
        return result;
    }

    /// <summary>Custom properties of an open document (file-level, then the active configuration's), plus a part's material.</summary>
    public Dictionary<string, string> ReadProperties(IModelDoc2 doc, string? configurationName = null)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void ReadFrom(string configuration, bool overwrite)
        {
            var manager = doc.Extension.get_CustomPropertyManager(configuration);
            if (manager == null || !(manager.GetNames() is Array names)) return;
            foreach (var item in names)
            {
                var name = item?.ToString();
                if (string.IsNullOrEmpty(name) || (!overwrite && properties.ContainsKey(name!))) continue;
                manager.Get5(name, false, out var value, out var resolved, out _);
                var text = string.IsNullOrEmpty(resolved) ? value : resolved;
                if (!string.IsNullOrWhiteSpace(text)) properties[name!] = text;
            }
        }

        try
        {
            ReadFrom("", overwrite: true);
            var configuration = string.IsNullOrEmpty(configurationName) ? doc.ConfigurationManager?.ActiveConfiguration?.Name : configurationName;
            if (!string.IsNullOrEmpty(configuration)) ReadFrom(configuration!, overwrite: true);

            if (doc.GetType() == (int)swDocumentTypes_e.swDocPART && doc is IPartDoc part)
            {
                var material = part.GetMaterialPropertyName2(configuration ?? "", out _);
                if (!string.IsNullOrWhiteSpace(material)) properties["Material"] = material;
            }
        }
        catch (Exception ex)
        {
            AddinLog.Write($"Couldn't read properties of {doc.GetPathName()}: {ex.Message}");
        }
        return properties;
    }

    /// <summary>
    /// Flattened, parts-only bill of materials of an open assembly: every part at any level, counted
    /// per configuration. Suppressed, envelope and excluded-from-BOM components (and everything inside
    /// them) are left out. Properties come from the loaded part, or are left empty if it's lightweight.
    /// </summary>
    public List<BomLine> GetBom(IModelDoc2 assembly)
    {
        var lines = new Dictionary<string, BomLine>(StringComparer.OrdinalIgnoreCase);
        if (!(assembly is IAssemblyDoc asm) || !(asm.GetComponents(false) is Array components)) return new List<BomLine>();

        foreach (var item in components)
        {
            if (!(item is IComponent2 component) || Excluded(component)) continue;
            var path = component.GetPathName() ?? "";
            if (!Path.GetExtension(path).Equals(".sldprt", StringComparison.OrdinalIgnoreCase)) continue;

            var configuration = component.ReferencedConfiguration ?? "";
            var key = path + "|" + configuration;
            if (!lines.TryGetValue(key, out var line))
            {
                line = new BomLine { Path = path, Configuration = configuration };
                if (component.GetModelDoc2() is IModelDoc2 part) line.Properties = ReadProperties(part, configuration);
                lines[key] = line;
            }
            line.Quantity++;
        }
        return lines.Values.ToList();
    }

    private static bool Excluded(IComponent2 component)
    {
        for (var c = component; c != null; c = c.GetParent())
        {
            if (c.IsSuppressed() || c.ExcludeFromBOM || c.IsEnvelope()) return true;
        }
        return false;
    }

    /// <summary>Reads properties from a file, opening it invisibly and read-only if it isn't open already.</summary>
    public Dictionary<string, string>? ReadPropertiesFromFile(string path)
    {
        var doc = FindOpen(path);
        var openedHere = false;
        if (doc == null)
        {
            var type = DocumentType(path);
            if (type == swDocumentTypes_e.swDocNONE || !File.Exists(path)) return null;

            var options = (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly);
            if (type == swDocumentTypes_e.swDocASSEMBLY)
            {
                options |= (int)(swOpenDocOptions_e.swOpenDocOptions_OverrideDefaultLoadLightweight | swOpenDocOptions_e.swOpenDocOptions_LoadLightweight);
            }
            _sw.DocumentVisible(false, (int)type);
            try
            {
                int errors = 0, warnings = 0;
                doc = _sw.OpenDoc6(path, (int)type, options, "", ref errors, ref warnings);
                openedHere = doc != null;
            }
            finally
            {
                _sw.DocumentVisible(true, (int)type);
            }
            if (doc == null) return null;
        }

        try
        {
            return ReadProperties(doc);
        }
        finally
        {
            if (openedHere) _sw.CloseDoc(path);
        }
    }

    /// <summary>
    /// Writes the Description custom property (file level, so every configuration shares it). An open
    /// document is only marked as changed (check-in saves it); a closed one is opened invisibly, saved
    /// and closed.
    /// </summary>
    public void SetDescription(string path, string description) => SetProperty(path, IndexEntry.DescriptionProperty, description);

    /// <summary>Writes a file-level custom property (e.g. PartNumber), the same way as <see cref="SetDescription"/>.</summary>
    public void SetProperty(string path, string name, string value)
    {
        var doc = FindOpen(path);
        var openedHere = false;
        if (doc == null)
        {
            var type = DocumentType(path);
            if (type == swDocumentTypes_e.swDocNONE || !File.Exists(path)) throw new PdmException($"{Path.GetFileName(path)} isn't a SolidWorks file on this PC.");
            var options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent;
            _sw.DocumentVisible(false, (int)type);
            try
            {
                int errors = 0, warnings = 0;
                doc = _sw.OpenDoc6(path, (int)type, options, "", ref errors, ref warnings);
                openedHere = doc != null;
            }
            finally
            {
                _sw.DocumentVisible(true, (int)type);
            }
            if (doc == null) throw new PdmException($"SolidWorks couldn't open {Path.GetFileName(path)} to save its {name} property.");
        }

        try
        {
            var manager = doc.Extension.get_CustomPropertyManager("");
            manager.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value,
                (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            if (!openedHere)
            {
                doc.SetSaveFlag();
                return;
            }
            int saveErrors = 0, saveWarnings = 0;
            if (!doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings))
            {
                throw new PdmException($"SolidWorks couldn't save the {name} property into {Path.GetFileName(path)} (error {saveErrors}).");
            }
        }
        finally
        {
            if (openedHere) _sw.CloseDoc(path);
        }
    }

    /// <summary>
    /// Points a closed assembly/drawing/derived part at a renamed or moved file. Writes the referencing
    /// file on disk, so it must be closed and checked out.
    /// </summary>
    public bool ReplaceReference(string referencingFile, string oldReference, string newReference) =>
        _sw.ReplaceReferencedDocument(referencingFile, oldReference, newReference);

    public static bool CanHavePreview(string path)
    {
        var type = DocumentType(path);
        return type == swDocumentTypes_e.swDocPART || type == swDocumentTypes_e.swDocASSEMBLY;
    }

    /// <summary>
    /// Saves an open part/assembly as a small binary STL mesh for the app's 3D viewer. Uses coarse
    /// quality and one file for assemblies, then puts the user's own STL export settings back. A mesh
    /// that's still large (usually an assembly) is simplified to <see cref="Jarvis.Core.Preview.PreviewSimplifier.MaxTriangles"/>.
    /// </summary>
    public bool ExportPreview(IModelDoc2 doc, string stlPath)
    {
        if (!CanHavePreview(doc.GetPathName())) return false;
        var toggles = new[]
        {
            (swUserPreferenceToggle_e.swSTLBinaryFormat, true),
            (swUserPreferenceToggle_e.swSTLComponentsIntoOneFile, true),
            (swUserPreferenceToggle_e.swSTLDontTranslateToPositive, true),
            (swUserPreferenceToggle_e.swSTLShowInfoOnSave, false),
            (swUserPreferenceToggle_e.swSTLPreview, false),
            (swUserPreferenceToggle_e.swSTLCheckForInterference, false),
        };
        var savedToggles = toggles.Select(t => (t.Item1, _sw.GetUserPreferenceToggle((int)t.Item1))).ToList();
        var savedQuality = _sw.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swSTLQuality);
        Action? cleanup = null;
        try
        {
            foreach (var (toggle, value) in toggles) _sw.SetUserPreferenceToggle((int)toggle, value);
            _sw.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swSTLQuality, (int)swSTLQuality_e.swSTLQuality_Coarse);

            var folder = Path.GetDirectoryName(stlPath)!;
            Directory.CreateDirectory(folder);
            var before = new HashSet<string>(Directory.EnumerateFiles(folder), StringComparer.OrdinalIgnoreCase);
            cleanup = () =>
            {
                foreach (var extra in Directory.EnumerateFiles(folder).Where(f => !before.Contains(f) && !string.Equals(f, stlPath, StringComparison.OrdinalIgnoreCase)))
                {
                    try { File.Delete(extra); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            };
            int errors = 0, warnings = 0;
            var saved = doc.Extension.SaveAs(stlPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, ref errors, ref warnings);
            if (!saved || !File.Exists(stlPath)) return false;
            FixNameCase(stlPath);
            // Assemblies with detailed hardware (threads, knurling) come out huge; keep previews small for the vault's storage.
            try
            {
                var (fullSize, simplified) = Jarvis.Core.Preview.PreviewSimplifier.ShrinkIfLarge(stlPath);
                if (simplified < fullSize) AddinLog.Write($"3D preview of {Path.GetFileName(doc.GetPathName())} simplified from {fullSize} to {simplified} triangles");
            }
            catch (Exception ex)
            {
                AddinLog.Write($"3D preview of {doc.GetPathName()} kept at full size (simplifying failed): {ex.Message}");
            }
            return true;
        }
        catch (Exception ex)
        {
            AddinLog.Write($"3D preview export failed for {doc.GetPathName()}: {ex.Message}");
            return false;
        }
        finally
        {
            foreach (var (toggle, value) in savedToggles) _sw.SetUserPreferenceToggle((int)toggle, value);
            _sw.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swSTLQuality, savedQuality);
            cleanup?.Invoke();
        }
    }

    /// <summary>
    /// SolidWorks saves "x.STL" even when asked for "x.stl". Windows doesn't care but Git does, so
    /// rename the file to exactly the expected name.
    /// </summary>
    private static void FixNameCase(string expectedPath)
    {
        var folder = Path.GetDirectoryName(expectedPath)!;
        var expectedName = Path.GetFileName(expectedPath);
        var actual = Directory.EnumerateFiles(folder).Select(Path.GetFileName)
            .FirstOrDefault(n => string.Equals(n, expectedName, StringComparison.OrdinalIgnoreCase));
        if (actual == null || string.Equals(actual, expectedName, StringComparison.Ordinal)) return;
        var temporary = expectedPath + ".renaming";
        File.Move(Path.Combine(folder, actual), temporary);
        File.Move(temporary, expectedPath);
    }

    /// <summary>
    /// Reads everything the vault index needs from one file, opening it invisibly once if it isn't
    /// open: properties, references, saved-in version, and (for parts/assemblies) the 3D preview.
    /// </summary>
    public IndexEntry IndexFile(string path, string vaultRoot, string? previewPath, out bool previewSaved)
    {
        previewSaved = false;
        var doc = FindOpen(path);
        var openedHere = false;
        var type = DocumentType(path);
        if (doc == null && type != swDocumentTypes_e.swDocNONE && File.Exists(path))
        {
            _sw.DocumentVisible(false, (int)type);
            try
            {
                int errors = 0, warnings = 0;
                // Fully resolved (not lightweight) so assemblies have geometry for the 3D preview.
                doc = _sw.OpenDoc6(path, (int)type, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
                openedHere = doc != null;
            }
            finally
            {
                _sw.DocumentVisible(true, (int)type);
            }
        }

        try
        {
            var properties = doc != null ? ReadProperties(doc) : null;
            if (doc != null && previewPath != null) previewSaved = ExportPreview(doc, previewPath);
            return BuildIndexEntry(path, vaultRoot, properties);
        }
        finally
        {
            if (openedHere) _sw.CloseDoc(path);
        }
    }

    /// <summary>Saves every sheet of a drawing as a PDF (opening the drawing invisibly if needed).</summary>
    public void ExportPdf(string drawingPath, string pdfPath)
    {
        var doc = FindOpen(drawingPath);
        var openedHere = false;
        if (doc == null)
        {
            _sw.DocumentVisible(false, (int)swDocumentTypes_e.swDocDRAWING);
            try
            {
                int openErrors = 0, openWarnings = 0;
                doc = _sw.OpenDoc6(drawingPath, (int)swDocumentTypes_e.swDocDRAWING,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref openErrors, ref openWarnings);
                openedHere = doc != null;
            }
            finally
            {
                _sw.DocumentVisible(true, (int)swDocumentTypes_e.swDocDRAWING);
            }
            if (doc == null) throw new PdmException($"SolidWorks couldn't open {Path.GetFileName(drawingPath)} to make its PDF.");
        }

        try
        {
            var exportData = _sw.GetExportFileData((int)swExportDataFileType_e.swExportPdfData) as IExportPdfData;
            exportData?.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportAllSheets, null);
            int errors = 0, warnings = 0;
            var saved = doc.Extension.SaveAs(pdfPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, exportData, ref errors, ref warnings);
            if (!saved || !File.Exists(pdfPath))
            {
                throw new PdmException($"SolidWorks couldn't save a PDF of {Path.GetFileName(drawingPath)} (error {errors}).");
            }
        }
        finally
        {
            if (openedHere) _sw.CloseDoc(drawingPath);
        }
    }

    /// <summary>Builds the vault index entry for a file: saved-in version, direct references, properties.</summary>
    public IndexEntry BuildIndexEntry(string fullPath, string vaultRoot, Dictionary<string, string>? properties)
    {
        var entry = new IndexEntry { SavedIn = SavedInYear(fullPath) };
        var prefix = vaultRoot.TrimEnd('\\') + "\\";
        foreach (var reference in GetReferences(fullPath, allLevels: false, includeParts: true))
        {
            if (reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) entry.References.Add(reference.Substring(prefix.Length).Replace('\\', '/'));
            else entry.ExternalReferences.Add(reference);
        }
        if (properties != null) entry.Properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase);
        return entry;
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
            if ((mode == CloseMode.Save || mode == CloseMode.SaveWritableDiscardReadOnly) && !doc.IsOpenedReadOnly())
            {
                Save(doc);
                continue;
            }
            if (mode == CloseMode.SaveWritableDiscardReadOnly) continue;
            if (mode == CloseMode.DiscardTargets && targetSet.Contains(path)) continue;
            if (mode == CloseMode.DiscardReadOnly && doc.IsOpenedReadOnly()) continue;
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

    /// <summary>
    /// Points the given component instances of an open assembly at another file (SolidWorks' Replace
    /// Components, keeping mates), live: nothing is closed or reopened. Works on a read-only assembly
    /// too; it's only saved if the assembly is checked out and the user saves.
    /// </summary>
    public bool ReplaceInstances(string assemblyPath, IEnumerable<string> instanceNames, string newPath)
    {
        var doc = FindOpen(assemblyPath) ?? throw new PdmException($"{Path.GetFileName(assemblyPath)} isn't open.");
        if (!(doc is IAssemblyDoc assembly) || !(assembly.GetComponents(false) is Array all)) return false;
        var names = new HashSet<string>(instanceNames, StringComparer.OrdinalIgnoreCase);
        var components = all.OfType<IComponent2>().Where(c => names.Contains(c.Name2 ?? "")).ToList();
        if (components.Count == 0) return false;

        doc.ClearSelection2(true);
        var first = true;
        foreach (var component in components)
        {
            component.Select4(!first, null, false);
            first = false;
        }
        var replaced = assembly.ReplaceComponents2(newPath, "", false, (int)swReplaceComponentsConfiguration_e.swReplaceComponentsConfiguration_MatchName, true);
        doc.ClearSelection2(true);
        return replaced;
    }

    /// <summary>Open, read-only (not checked out) documents using these files that SolidWorks marks as changed.</summary>
    public List<string> UnsavedReadOnlyDocumentsUsing(IEnumerable<string> targets)
    {
        var targetSet = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        return VisibleDocuments()
            .Where(doc => doc.GetSaveFlag() && doc.IsOpenedReadOnly())
            .Select(doc => doc.GetPathName())
            .Where(path => targetSet.Contains(path) || GetReferences(path).Any(targetSet.Contains))
            .ToList();
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

    /// <summary>
    /// Saves the target files that have unsaved changes, wherever they're loaded (their own window or
    /// inside an assembly). Nothing is closed: SolidWorks doesn't lock files, so Git can read them, and
    /// assemblies that merely use a target (even with unsaved changes of their own) stay as they are.
    /// </summary>
    public void SaveTargets(IEnumerable<string> targets)
    {
        foreach (var path in targets)
        {
            var doc = FindOpen(path);
            if (doc == null || !doc.GetSaveFlag()) continue;
            if (doc.IsOpenedReadOnly()) throw new PdmException($"{Path.GetFileName(path)} has unsaved changes but is open read-only, so they can't be saved.");
            int errors = 0, warnings = 0;
            if (!doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
            {
                throw new PdmException($"SolidWorks couldn't save {Path.GetFileName(path)} (error {errors}). Save it yourself, then try again.");
            }
        }
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

    public static swDocumentTypes_e DocumentType(string path)
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
