using System.Windows;
using System.Windows.Media.Media3D;

namespace Jarvis.Client.Views;

public partial class Viewer3DWindow : Window
{
    public Viewer3DWindow(string title, Model3D model)
    {
        InitializeComponent();
        Title = "3D Preview — " + title;
        Viewer.Model = model;
    }
}
