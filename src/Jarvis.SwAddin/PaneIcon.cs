using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Jarvis.SwAddin;

/// <summary>
/// Writes the task pane tab icon (the vault safe, embedded from assets\vault.png) to disk,
/// because SolidWorks takes icons as file paths.
/// </summary>
internal static class PaneIcon
{
    /// <summary>The sizes CreateTaskpaneView3 expects, so the icon stays sharp at any display scaling.</summary>
    private static readonly int[] Sizes = { 20, 32, 40, 64, 96, 128 };

    private static string Folder => Path.Combine(Path.GetDirectoryName(AddinLog.FilePath)!, "icons");

    /// <summary>PNG paths for CreateTaskpaneView3 (SolidWorks 2017 and newer).</summary>
    public static string[] EnsurePngFiles()
    {
        Directory.CreateDirectory(Folder);
        using var master = LoadMaster();
        var paths = new string[Sizes.Length];
        for (var i = 0; i < Sizes.Length; i++)
        {
            paths[i] = Path.Combine(Folder, $"taskpane_{Sizes[i]}.png");
            using var bitmap = Resize(master, Sizes[i], Sizes[i], Color.Transparent);
            bitmap.Save(paths[i], ImageFormat.Png);
        }
        return paths;
    }

    /// <summary>16x18 bitmap for the older CreateTaskpaneView2, used only if View3 fails.</summary>
    public static string EnsureBmpFile()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "taskpane_16x18.bmp");
        using var master = LoadMaster();
        using var bitmap = Resize(master, 16, 18, SystemColors.Control);
        bitmap.Save(path, ImageFormat.Bmp);
        return path;
    }

    /// <summary>The safe, scaled for the pane header.</summary>
    public static Bitmap LoadLogo(int size)
    {
        using var master = LoadMaster();
        return Resize(master, size, size, Color.Transparent);
    }

    private static Image LoadMaster()
    {
        using var stream = typeof(PaneIcon).Assembly.GetManifestResourceStream("Jarvis.SwAddin.vault.png")!;
        // Copy so the image doesn't depend on the resource stream staying open.
        return new Bitmap(Image.FromStream(stream));
    }

    private static Bitmap Resize(Image source, int width, int height, Color background)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(background);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var size = System.Math.Min(width, height);
        g.DrawImage(source, (width - size) / 2, (height - size) / 2, size, size);
        return bitmap;
    }
}
