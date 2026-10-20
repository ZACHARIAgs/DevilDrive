using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;

namespace CapstonePdm.SwAddin;

/// <summary>
/// Entry point SolidWorks calls. Registered with RegAsm (see scripts\Install-SolidWorksAddin.ps1).
/// Adds the "Jarvis" task pane; all PDM work happens in <see cref="VaultPane"/>.
/// </summary>
[ComVisible(true)]
[Guid("9BD312D2-4FE8-4BEF-AB6C-91F6EA2CFB78")]
[ProgId("CapstonePdm.SwAddin")]
public sealed class CapstonePdmAddin : ISwAddin
{
    private const string Title = "Jarvis";
    private const string Description = "Check SolidWorks files in and out of your team's vault";

    private ISldWorks? _sw;
    private ITaskpaneView? _taskPane;
    private VaultPane? _pane;

    public CapstonePdmAddin()
    {
        // Must run before any Core or System.Text.Json type is touched.
        AssemblyResolver.Install();
    }

    public bool ConnectToSW(object ThisSW, int Cookie)
    {
        try
        {
            _sw = (ISldWorks)ThisSW;
            AddinLog.Write($"Loaded in SolidWorks {_sw.RevisionNumber()}");
            CreatePane(_sw);
            return true;
        }
        catch (Exception ex)
        {
            AddinLog.Write("Failed to load: " + ex);
            return false;
        }
    }

    public bool DisconnectFromSW()
    {
        try
        {
            _pane?.Shutdown();
            _taskPane?.DeleteView();
            if (_taskPane != null) Marshal.ReleaseComObject(_taskPane);
        }
        catch (Exception ex)
        {
            AddinLog.Write("Error while unloading: " + ex);
        }
        _pane = null;
        _taskPane = null;
        _sw = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return true;
    }

    private void CreatePane(ISldWorks sw)
    {
        try
        {
            _taskPane = sw.CreateTaskpaneView3(PaneIcon.EnsurePngFiles(), Title);
        }
        catch (Exception ex)
        {
            AddinLog.Write("Multi-size task pane icon failed, using the basic one: " + ex.Message);
        }
        _taskPane ??= sw.CreateTaskpaneView2(PaneIcon.EnsureBmpFile(), Title);
        _pane = new VaultPane(new SolidWorksSession(sw));
        _taskPane.DisplayWindowFromHandlex64(_pane.Handle.ToInt64());
    }

    // ------------------------------------------------------------ registration (RegAsm)

    [ComRegisterFunction]
    public static void Register(Type type)
    {
        using (var key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\SolidWorks\Addins\{{{type.GUID}}}"))
        {
            key.SetValue(null, 0, RegistryValueKind.DWord);
            key.SetValue("Title", Title);
            key.SetValue("Description", Description);
        }
        // Load at startup for the user running the installer.
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\SolidWorks\AddInsStartup\{{{type.GUID}}}"))
        {
            key.SetValue(null, 1, RegistryValueKind.DWord);
        }
    }

    [ComUnregisterFunction]
    public static void Unregister(Type type)
    {
        Registry.LocalMachine.DeleteSubKey($@"SOFTWARE\SolidWorks\Addins\{{{type.GUID}}}", false);
        Registry.CurrentUser.DeleteSubKey($@"Software\SolidWorks\AddInsStartup\{{{type.GUID}}}", false);
    }
}

/// <summary>
/// SolidWorks is the host process, so our app.config binding redirects don't apply. Resolve any
/// dependency (System.Text.Json and friends) from the add-in folder, whatever version is asked for.
/// </summary>
internal static class AssemblyResolver
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
    }

    private static Assembly? OnResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (name == null || name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

        var folder = Path.GetDirectoryName(typeof(AssemblyResolver).Assembly.Location);
        var file = Path.Combine(folder ?? "", name + ".dll");
        if (!File.Exists(file)) return null;
        try
        {
            return Assembly.LoadFrom(file);
        }
        catch (Exception ex)
        {
            AddinLog.Write($"Couldn't load {file}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>%LocalAppData%\CapstonePDM\addin.log: the first place to look when the add-in misbehaves.</summary>
internal static class AddinLog
{
    public static string FilePath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "CapstonePDM", "addin.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 1_000_000) info.Delete();
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{System.Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never break SolidWorks.
        }
    }
}
