using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using Jarvis.Client.Services;
using Jarvis.Core.Preview;
using HelixToolkit.Wpf;

namespace Jarvis.PreviewHandler;

/// <summary>The preview pane's contents: the rotatable 3D model, or the SolidWorks picture as a fallback.</summary>
internal sealed class PreviewControl : UserControl
{
    private readonly Label _caption = new Label
    {
        Dock = DockStyle.Bottom,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(0x6B, 0x72, 0x80),
        BackColor = Color.FromArgb(0xDD, 0xE3, 0xEA),
        Font = new Font("Segoe UI", 8.25f),
    };

    public PreviewControl()
    {
        BackColor = Color.White;
        _caption.Height = TextRenderer.MeasureText("Ag", _caption.Font).Height + 14; // follows the screen DPI
        Controls.Add(_caption);
    }

    public void ShowFile(string path)
    {
        var stl = Vaults.PreviewFor(path);
        if (stl != null)
        {
            try
            {
                ShowModel(stl);
                _caption.Text = "Drag to rotate \u2022 scroll to zoom";
                return;
            }
            catch (Exception ex)
            {
                ModelPreviewHandler.Log($"3D preview of {path} failed: {ex.Message}");
            }
        }
        ShowPicture(path);
        _caption.Text = Vaults.IsInVault(path) ? "No 3D preview yet (it's made when the file is checked in from SolidWorks)" : "";
    }

    private void ShowModel(string stlPath)
    {
        var mesh = PreviewMesh.Load(stlPath);
        if (mesh.TriangleCount == 0) throw new InvalidDataException("empty preview");

        var key = new DirectionalLight(System.Windows.Media.Color.FromRgb(0xC4, 0xC4, 0xC0), new Vector3D(-1, -1, -1));
        var lights = new Model3DGroup();
        lights.Children.Add(PreviewModel.Lights());
        lights.Children.Add(key);

        var viewport = new HelixViewport3D
        {
            Background = PreviewModel.Background(),
            ShowViewCube = false,
            ShowCoordinateSystem = false,
            IsHeadLightEnabled = false,
            ZoomExtentsWhenLoaded = true,
            RotateGesture = new MouseGesture(MouseAction.LeftClick),
            PanGesture = new MouseGesture(MouseAction.RightClick),
            ModelUpDirection = new Vector3D(0, 1, 0),
            Camera = new PerspectiveCamera(new Point3D(100, 80, 110), new Vector3D(-100, -80, -110), new Vector3D(0, 1, 0), 30),
        };
        viewport.Children.Add(new ModelVisual3D { Content = lights });
        viewport.Children.Add(new ModelVisual3D { Content = PreviewModel.Build(mesh) });

        void AimKeyLight()
        {
            if (!(viewport.Camera is ProjectionCamera camera)) return;
            var look = camera.LookDirection;
            look.Normalize();
            var up = camera.UpDirection;
            up.Normalize();
            var right = Vector3D.CrossProduct(look, up);
            right.Normalize();
            key.Direction = look - up * 0.45 + right * 0.3;
        }
        viewport.CameraChanged += (_, _) => AimKeyLight();
        viewport.Loaded += (_, _) =>
        {
            viewport.ZoomExtents();
            AimKeyLight();
        };

        var host = new ElementHost { Dock = DockStyle.Fill, Child = viewport };
        Controls.Add(host);
        host.BringToFront();
    }

    /// <summary>The picture Windows would show (SolidWorks' own thumbnail).</summary>
    private void ShowPicture(string path)
    {
        try
        {
            Native.SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            if (factory.GetImage(new SIZE { cx = 512, cy = 512 }, 0, out var hbitmap) != 0 || hbitmap == IntPtr.Zero) return;
            try
            {
                var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = Image.FromHbitmap(hbitmap) };
                Controls.Add(picture);
                picture.BringToFront();
            }
            finally
            {
                Native.DeleteObject(hbitmap);
            }
        }
        catch (Exception ex)
        {
            ModelPreviewHandler.Log($"Picture of {path} failed: {ex.Message}");
        }
    }
}
