using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace CapstonePdm.Client.Services;

/// <summary>Loads the add-in's STL previews off the UI thread.</summary>
internal static class StlLoader
{
    public static Task<Model3D?> LoadAsync(string path) => Task.Run(() =>
    {
        var reader = new StLReader { DefaultMaterial = SolidMaterial() };
        var model = reader.Read(path);
        if (model == null) return null;
        MakeSolid(model);
        model.Freeze(); // frozen, so the UI thread (and the pop-out window) can use it
        return (Model3D?)model;
    });

    /// <summary>
    /// Plain matte grey. A shiny (specular) layer looks nicer but makes WPF show hidden edges through
    /// the surface, so the part looked see-through.
    /// </summary>
    public static Material SolidMaterial()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xAC, 0xB4, 0xC0));
        brush.Freeze();
        return new DiffuseMaterial(brush);
    }

    /// <summary>
    /// Draws both sides of every triangle. Otherwise, when a mesh's triangles face inward (it varies by
    /// exporter), WPF skips the outer surfaces and the part looks see-through.
    /// </summary>
    public static void MakeSolid(Model3D model)
    {
        switch (model)
        {
            case Model3DGroup group:
                foreach (var child in group.Children) MakeSolid(child);
                break;
            case GeometryModel3D geometry:
                geometry.BackMaterial = geometry.Material;
                break;
        }
    }
}
