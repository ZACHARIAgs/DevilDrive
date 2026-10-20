using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Jarvis.Client.Services;

/// <summary>
/// Gets the same preview Windows Explorer shows. SolidWorks (and eDrawings) install the thumbnail
/// handler for .sldprt/.sldasm/.slddrw, so this needs no SolidWorks code.
/// </summary>
internal static class ShellThumbnail
{
    /// <summary>Runs on its own STA thread (the shell requires it) so the UI never waits on a slow handler.</summary>
    public static Task<BitmapSource?> GetAsync(string path, int size)
    {
        var tcs = new TaskCompletionSource<BitmapSource?>();
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Get(path, size));
            }
            catch (Exception)
            {
                tcs.SetResult(null);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static BitmapSource? Get(string path, int size)
    {
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        try
        {
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, ImageFlags.BiggerSizeOk, out var bitmap) != 0) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ImageFlags flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [Flags]
    private enum ImageFlags
    {
        ResizeToFit = 0,
        BiggerSizeOk = 1,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
