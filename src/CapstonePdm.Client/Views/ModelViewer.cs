using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CapstonePdm.Client.Services;
using HelixToolkit.Wpf;

namespace CapstonePdm.Client.Views;

/// <summary>
/// A 3D view of a part/assembly preview: drag to rotate, right-drag to pan, scroll to zoom.
/// SolidWorks models are Y-up, so the view is too. A key light follows the camera (like SolidWorks),
/// so whichever side you turn to is lit; soft fixed fill lights keep the shape readable.
/// </summary>
public sealed class ModelViewer : HelixViewport3D
{
    public static readonly DependencyProperty ModelProperty =
        DependencyProperty.Register(nameof(Model), typeof(Model3D), typeof(ModelViewer), new PropertyMetadata(null, OnModelChanged));

    private readonly ModelVisual3D _visual = new ModelVisual3D();
    private readonly DirectionalLight _key = new DirectionalLight(Color.FromRgb(0xC4, 0xC4, 0xC0), new Vector3D(-1, -1, -1));

    public ModelViewer()
    {
        var lights = new Model3DGroup();
        lights.Children.Add(PreviewModel.Lights());
        lights.Children.Add(_key);
        Children.Add(new ModelVisual3D { Content = lights });
        Children.Add(_visual);
        Background = PreviewModel.Background();
        ShowViewCube = true;
        ShowCoordinateSystem = false;
        ShowFrameRate = false;
        IsHeadLightEnabled = false;
        ZoomExtentsWhenLoaded = true;
        RotateGesture = new MouseGesture(MouseAction.LeftClick);
        PanGesture = new MouseGesture(MouseAction.RightClick);
        ModelUpDirection = new Vector3D(0, 1, 0);
        Camera = new PerspectiveCamera
        {
            Position = new Point3D(100, 100, 100),
            LookDirection = new Vector3D(-100, -100, -100),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 30,
        };
        CameraChanged += (_, _) => AimKeyLight();
        Loaded += (_, _) =>
        {
            ZoomExtents();
            AimKeyLight();
        };
    }

    public Model3D? Model
    {
        get => (Model3D?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Key light from over the viewer's shoulder (slightly above and to the left of the camera).</summary>
    private void AimKeyLight()
    {
        if (!(Camera is ProjectionCamera camera)) return;
        var look = camera.LookDirection;
        look.Normalize();
        var up = camera.UpDirection;
        up.Normalize();
        var right = Vector3D.CrossProduct(look, up);
        right.Normalize();
        _key.Direction = look - up * 0.45 + right * 0.3;
    }

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ModelViewer)d;
        viewer._visual.Content = e.NewValue as Model3D;
        if (viewer.IsLoaded && e.NewValue != null) viewer.ZoomExtents();
    }
}
