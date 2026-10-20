using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CapstonePdm.PreviewHandler;

/// <summary>
/// File Explorer preview handler for SolidWorks parts and assemblies. Vault files show their 3D
/// preview (the STL the add-in saves under .pdm\preview) that you can rotate; anything else shows the
/// normal SolidWorks picture. Registered per user by the desktop app.
/// </summary>
[ComVisible(true)]
[Guid(Clsid)]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("CapstonePdm.ModelPreviewHandler")]
public sealed class ModelPreviewHandler : IPreviewHandler, IInitializeWithFile, IObjectWithSite, IOleWindow
{
    public const string Clsid = "6E3F2B9A-1C4D-4E8B-9F27-5A0C3D81B7E4";

    private string? _path;
    private IntPtr _parent;
    private RECT _rect;
    private object? _site;
    private PreviewControl? _control;

    static ModelPreviewHandler()
    {
        // prevhost.exe looks for assemblies next to itself (System32); point it at our folder for HelixToolkit.
        var folder = Path.GetDirectoryName(new Uri(typeof(ModelPreviewHandler).Assembly.CodeBase).LocalPath)!;
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var file = Path.Combine(folder, new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
    }

    public void Initialize(string pszFilePath, uint grfMode) => _path = pszFilePath;

    public void SetWindow(IntPtr hwnd, ref RECT rect)
    {
        _parent = hwnd;
        _rect = rect;
        if (_control != null)
        {
            Native.SetParent(_control.Handle, _parent);
            _control.Bounds = _rect.ToRectangle();
        }
    }

    public void SetRect(ref RECT rect)
    {
        _rect = rect;
        if (_control != null) _control.Bounds = _rect.ToRectangle();
    }

    public void DoPreview()
    {
        if (_path == null) return;
        try
        {
            _control = new PreviewControl();
            _control.CreateControl();
            Native.SetParent(_control.Handle, _parent);
            _control.Bounds = _rect.ToRectangle();
            _control.ShowFile(_path);
            _control.Visible = true;
        }
        catch (Exception ex)
        {
            Log("Preview failed for " + _path + ": " + ex);
        }
    }

    public void Unload()
    {
        _control?.Dispose();
        _control = null;
    }

    public void SetFocus() => _control?.Focus();

    public void QueryFocus(out IntPtr phwnd) => phwnd = _control?.Handle ?? IntPtr.Zero;

    public uint TranslateAccelerator(ref MSG pmsg) => 1; // S_FALSE: not handled

    public void SetSite(object pUnkSite) => _site = pUnkSite;

    public void GetSite(ref Guid riid, out object ppvSite) => ppvSite = _site!;

    public void GetWindow(out IntPtr phwnd) => phwnd = _control?.Handle ?? IntPtr.Zero;

    public void ContextSensitiveHelp(bool fEnterMode)
    {
    }

    internal static void Log(string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapstonePDM");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "preview.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never break the preview.
        }
    }
}

/// <summary>Finds a file's vault and its 3D preview, using the vault list the desktop app writes.</summary>
internal static class Vaults
{
    public static string ListFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CapstonePDM", "vaults.txt");

    public static bool IsInVault(string filePath) => VaultOf(Path.GetFullPath(filePath)) != null;

    private static string? VaultOf(string full)
    {
        if (!File.Exists(ListFile)) return null;
        return File.ReadAllLines(ListFile)
            .Select(l => l.Trim().TrimEnd('\\'))
            .Where(l => l.Length > 0 && full.StartsWith(l + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Length)
            .FirstOrDefault();
    }

    /// <summary>The add-in's STL preview for this file, or null.</summary>
    public static string? PreviewFor(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var vault = VaultOf(full);
        if (vault == null) return null;
        var relative = full.Substring(vault.Length + 1);
        var stl = Path.Combine(vault, ".pdm", "preview", relative + ".stl");
        return File.Exists(stl) ? stl : null;
    }
}
