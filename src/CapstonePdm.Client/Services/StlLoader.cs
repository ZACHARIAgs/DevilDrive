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
        var reader = new StLReader { DefaultMaterial = MaterialHelper.CreateMaterial(Color.FromRgb(0xB0, 0xB8, 0xC4), 40) };
        var model = reader.Read(path);
        if (model == null) return null;
        model.Freeze(); // frozen, so the UI thread (and the pop-out window) can use it
        return (Model3D?)model;
    });
}
