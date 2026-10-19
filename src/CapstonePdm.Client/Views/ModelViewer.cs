using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace CapstonePdm.Client.Views;

/// <summary>
/// A 3D view of a part/assembly preview: drag to rotate, right-drag to pan, scroll to zoom.
/// SolidWorks models are Y-up, so the view is too.
/// </summary>
public sealed class ModelViewer : HelixViewport3D
{
    public static readonly DependencyProperty ModelProperty =
        DependencyProperty.Register(nameof(Model), typeof(Model3D), typeof(ModelViewer), new PropertyMetadata(null, OnModelChanged));

    private readonly ModelVisual3D _visual = new ModelVisual3D();

    public ModelViewer()
    {
        Children.Add(new DefaultLights());
        Children.Add(_visual);
        Background = Brushes.White;
        ShowViewCube = true;
        ShowCoordinateSystem = false;
        ShowFrameRate = false;
        IsHeadLightEnabled = true;
        ZoomExtentsWhenLoaded = true;
        RotateGesture = new MouseGesture(MouseAction.LeftClick);
        PanGesture = new MouseGesture(MouseAction.RightClick);
        ModelUpDirection = new Vector3D(0, 1, 0);
        Camera = new PerspectiveCamera
        {
            Position = new Point3D(100, 100, 100),
            LookDirection = new Vector3D(-100, -100, -100),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 40,
        };
        Loaded += (_, _) => ZoomExtents();
    }

    public Model3D? Model
    {
        get => (Model3D?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ModelViewer)d;
        viewer._visual.Content = e.NewValue as Model3D;
        if (viewer.IsLoaded && e.NewValue != null) viewer.ZoomExtents();
    }
}
