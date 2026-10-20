using System;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CapstonePdm.Core.Preview;

namespace CapstonePdm.Client.Services;

/// <summary>
/// Turns a <see cref="PreviewMesh"/> into a WPF model that looks like SolidWorks' "shaded with edges":
/// a light satin-grey surface with smooth shading and thin dark lines along the real edges.
/// Shared with the File Explorer preview handler (linked source file), so keep it WPF-only.
/// </summary>
public static class PreviewModel
{
    private static readonly Color Surface = Color.FromRgb(0xB9, 0xC2, 0xCE);
    private static readonly Color EdgeColor = Color.FromRgb(0x2F, 0x37, 0x42);

    public static Model3DGroup Build(PreviewMesh mesh)
    {
        var group = new Model3DGroup();
        if (mesh.TriangleCount == 0) return group;

        var surface = new MeshGeometry3D();
        for (var i = 0; i < mesh.Positions.Count; i += 3)
        {
            surface.Positions.Add(new Point3D(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]));
            surface.Normals.Add(new Vector3D(mesh.Normals[i], mesh.Normals[i + 1], mesh.Normals[i + 2]));
        }
        foreach (var index in mesh.Indices) surface.TriangleIndices.Add(index);
        surface.Freeze();

        // Satin finish: diffuse base plus a soft, wide highlight. The back side gets the plain diffuse
        // so inward-facing triangles (some exporters) still look solid.
        var diffuse = new DiffuseMaterial(Frozen(new SolidColorBrush(Surface)));
        var material = new MaterialGroup();
        material.Children.Add(diffuse);
        material.Children.Add(new SpecularMaterial(Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))), 28));
        group.Children.Add(new GeometryModel3D(surface, material) { BackMaterial = diffuse });

        if (mesh.EdgeCount > 0) group.Children.Add(EdgeLines(mesh));
        group.Freeze();
        return group;
    }

    /// <summary>Feature edges as thin square tubes (WPF 3D has no lines), sized from the model so they read at any scale.</summary>
    private static GeometryModel3D EdgeLines(PreviewMesh mesh)
    {
        var radius = Math.Max(mesh.Size * 0.0011, 1e-6);
        var lines = new MeshGeometry3D();
        var e = mesh.Edges;
        for (var i = 0; i < e.Count; i += 6)
        {
            var a = new Point3D(e[i], e[i + 1], e[i + 2]);
            var b = new Point3D(e[i + 3], e[i + 4], e[i + 5]);
            var axis = b - a;
            if (axis.Length < 1e-12) continue;
            axis.Normalize();
            // Two directions across the tube.
            var helper = Math.Abs(axis.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
            var u = Vector3D.CrossProduct(axis, helper);
            u.Normalize();
            var v = Vector3D.CrossProduct(axis, u);
            u *= radius;
            v *= radius;

            var start = lines.Positions.Count;
            foreach (var end in new[] { a, b })
            {
                lines.Positions.Add(end + u);
                lines.Positions.Add(end + v);
                lines.Positions.Add(end - u);
                lines.Positions.Add(end - v);
            }
            for (var side = 0; side < 4; side++)
            {
                int p0 = start + side, p1 = start + (side + 1) % 4, q0 = p0 + 4, q1 = p1 + 4;
                lines.TriangleIndices.Add(p0); lines.TriangleIndices.Add(p1); lines.TriangleIndices.Add(q1);
                lines.TriangleIndices.Add(p0); lines.TriangleIndices.Add(q1); lines.TriangleIndices.Add(q0);
            }
        }
        lines.Freeze();
        var edgeMaterial = new EmissiveMaterial(Frozen(new SolidColorBrush(EdgeColor)));
        var dark = new MaterialGroup();
        dark.Children.Add(new DiffuseMaterial(Frozen(new SolidColorBrush(Colors.Black))));
        dark.Children.Add(edgeMaterial);
        return new GeometryModel3D(lines, dark) { BackMaterial = dark };
    }

    /// <summary>Studio lighting: soft ambient, a key light from the upper front, a cool fill and a rim light.</summary>
    public static Model3DGroup Lights()
    {
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x46, 0x48, 0x4C)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xC8, 0xC8, 0xC4), new Vector3D(-0.6, -1.0, -0.8)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x5A, 0x60, 0x6A), new Vector3D(0.9, -0.2, 0.5)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x3C, 0x3C, 0x40), new Vector3D(0.2, 0.8, 1.0)));
        lights.Freeze();
        return lights;
    }

    /// <summary>Soft top-to-bottom background like SolidWorks' default.</summary>
    public static Brush Background()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xDD, 0xE3, 0xEA), 90);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
