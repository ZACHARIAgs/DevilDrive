using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace CapstonePdm.SwAddin;

/// <summary>Draws the 16x18 task pane tab icon (a blue padlock) on first use, so no image file has to ship.</summary>
internal static class PaneIcon
{
    public static string EnsureFile()
    {
        var path = Path.Combine(Path.GetDirectoryName(AddinLog.FilePath)!, "taskpane-icon.bmp");
        if (File.Exists(path)) return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var bitmap = new Bitmap(16, 18))
        using (var g = Graphics.FromImage(bitmap))
        using (var blue = new SolidBrush(Color.FromArgb(0x15, 0x65, 0xC0)))
        using (var pen = new Pen(Color.FromArgb(0x15, 0x65, 0xC0), 2f))
        {
            g.Clear(Color.White);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawArc(pen, 4, 2, 8, 9, 180, 180); // shackle
            g.DrawLine(pen, 4, 6, 4, 9);
            g.DrawLine(pen, 12, 6, 12, 9);
            g.FillRectangle(blue, 2, 8, 12, 8); // body
            g.FillRectangle(Brushes.White, 7, 10, 2, 4); // keyhole
            bitmap.Save(path, ImageFormat.Bmp);
        }
        return path;
    }
}
