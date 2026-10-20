using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace CapstonePdm.Client.Services;

/// <summary>
/// Installs the File Explorer preview pane add-on (3D model for .sldprt/.sldasm) for the current user:
/// the handler DLLs are embedded in this exe, written to %LocalAppData%\Programs\CapstonePDM\PreviewHandler
/// and registered under HKCU (no admin rights), the same way PowerToys registers its previewers.
/// Also covers the classic right-click menu switch.
/// </summary>
internal static class ExplorerPreview
{
    private const string Clsid = "{6E3F2B9A-1C4D-4E8B-9F27-5A0C3D81B7E4}";
    private const string PreviewHandlerShellEx = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const string PrevhostAppId = "{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}";
    private const string Name = "Capstone PDM 3D preview";
    private static readonly string[] Extensions = { ".sldprt", ".sldasm" };
    private static readonly string[] HandlerFiles = { "CapstonePdm.PreviewHandler.dll", "HelixToolkit.Wpf.dll" };

    /// <summary>Vault folders the preview handler looks in (it can't read settings.json without extra libraries).</summary>
    public static void WriteVaultList(IEnumerable<string> vaults)
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CapstonePDM", "vaults.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllLines(file, vaults);
    }

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extensions[0]}\ShellEx\{PreviewHandlerShellEx}");
        return string.Equals(key?.GetValue(null) as string, Clsid, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register()
    {
        var dll = ExtractHandler();
        var assemblyName = AssemblyName.GetAssemblyName(dll).FullName;
        using (var clsid = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{Clsid}"))
        {
            clsid.SetValue(null, Name);
            clsid.SetValue("AppID", PrevhostAppId);
            // Needed to be given the file path (IInitializeWithFile) to find the vault's 3D preview.
            clsid.SetValue("DisableLowILProcessIsolation", 1, RegistryValueKind.DWord);
            using var server = clsid.CreateSubKey("InprocServer32");
            server.SetValue(null, "mscoree.dll");
            server.SetValue("ThreadingModel", "Apartment");
            server.SetValue("Class", "CapstonePdm.PreviewHandler.ModelPreviewHandler");
            server.SetValue("Assembly", assemblyName);
            server.SetValue("RuntimeVersion", "v4.0.30319");
            server.SetValue("CodeBase", new Uri(dll).AbsoluteUri);
        }
        foreach (var extension in Extensions)
        {
            using var shellEx = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\ShellEx\{PreviewHandlerShellEx}");
            shellEx.SetValue(null, Clsid);
        }
        using var handlers = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers");
        handlers.SetValue(Clsid, Name);
    }

    public static void Unregister()
    {
        foreach (var extension in Extensions)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{extension}\ShellEx\{PreviewHandlerShellEx}", false);
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{Clsid}", false);
        using var handlers = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers", true);
        handlers?.DeleteValue(Clsid, false);
    }

    /// <summary>
    /// Writes the embedded handler into a folder named after its contents, so a new version never has
    /// to overwrite a DLL that File Explorer's preview host still has loaded.
    /// </summary>
    private static string ExtractHandler()
    {
        var assembly = typeof(ExplorerPreview).Assembly;
        var contents = HandlerFiles.Select(name =>
        {
            using var stream = assembly.GetManifestResourceStream("PreviewHandler/" + name)
                               ?? throw new InvalidOperationException($"{name} isn't embedded in this build.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return (Name: name, Bytes: memory.ToArray());
        }).ToList();

        var hash = Convert.ToHexString(SHA256.HashData(contents.SelectMany(c => c.Bytes).ToArray()))[..12].ToLowerInvariant();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CapstonePDM", "PreviewHandler");
        var folder = Path.Combine(root, hash);
        Directory.CreateDirectory(folder);
        foreach (var (name, bytes) in contents)
        {
            var target = Path.Combine(folder, name);
            if (!File.Exists(target) || new FileInfo(target).Length != bytes.Length) File.WriteAllBytes(target, bytes);
        }

        // Old versions: remove the ones nothing has loaded anymore.
        foreach (var old in Directory.GetDirectories(root).Where(d => !string.Equals(d, folder, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Still loaded by the preview host; it goes next time.
            }
        }
        return Path.Combine(folder, HandlerFiles[0]);
    }

    // ------------------------------------------------------------ classic right-click menu

    /// <summary>
    /// Windows 11 shows app menus under "Show more options". This per-user setting (the empty
    /// InprocServer32 override) brings back the full Windows 10-style menu everywhere, so Capstone
    /// PDM's commands show directly. Takes effect when File Explorer restarts.
    /// </summary>
    private const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    public static bool IsClassicMenu()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ClassicMenuKey + @"\InprocServer32");
        return key != null;
    }

    public static void SetClassicMenu(bool classic)
    {
        if (classic)
        {
            using var key = Registry.CurrentUser.CreateSubKey(ClassicMenuKey + @"\InprocServer32");
            key.SetValue(null, "");
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(ClassicMenuKey, false);
        }
    }

    /// <summary>Restarts File Explorer so menu changes apply (open Explorer windows close and the taskbar reloads).</summary>
    public static void RestartExplorer()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("explorer"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
        // Windows usually restarts the shell by itself; make sure it's back.
        System.Threading.Thread.Sleep(1500);
        if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
    }
}
