using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Jarvis.Core.Preview;

namespace Jarvis.Client.Services;

/// <summary>Loads the add-in's STL previews off the UI thread.</summary>
internal static class StlLoader
{
    public static Task<Model3D?> LoadAsync(string path) => Task.Run(() =>
    {
        var mesh = PreviewMesh.Load(path);
        if (mesh.TriangleCount == 0) return null;
        // Frozen, so the UI thread (and the pop-out window) can use it.
        return (Model3D?)PreviewModel.Build(mesh);
    });

    /// <summary>
    /// Renders a preview to a PNG without opening a window ("--render-preview in.stl out.png"), for
    /// checking how previews look. Isometric view, same lights and colors as the viewer.
    /// </summary>
    public static void RenderToPng(string stlPath, string pngPath, int width = 900, int height = 650)
    {
        var model = PreviewModel.Build(PreviewMesh.Load(stlPath));
        var bounds = model.Bounds;
        var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        var radius = Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) / 2;
        var look = new Vector3D(-1, -0.8, -1.1);
        look.Normalize();
        var distance = radius / Math.Tan(15 * Math.PI / 180) * 1.1;
        var camera = new PerspectiveCamera(center - look * distance, look, new Vector3D(0, 1, 0), 30);

        var up = new Vector3D(0, 1, 0);
        var right = Vector3D.CrossProduct(look, up);
        right.Normalize();
        var scene = new Model3DGroup();
        scene.Children.Add(PreviewModel.Lights());
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0xC4, 0xC4, 0xC0), look - up * 0.45 + right * 0.3));
        scene.Children.Add(model);

        var viewport = new Viewport3D { Camera = camera, Width = width, Height = height };
        viewport.Children.Add(new ModelVisual3D { Content = scene });
        var host = new Grid { Width = width, Height = height, Background = PreviewModel.Background() };
        host.Children.Add(viewport);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(pngPath);
        encoder.Save(file);
    }
}
