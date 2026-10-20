using System;
using System.Collections.Generic;
using System.Linq;
using CapstonePdm.Core.Preview;
using Xunit;

namespace CapstonePdm.Core.Tests;

public class PreviewMeshTests
{
    private static Vec3 V(double x, double y, double z) => new Vec3(x, y, z);

    /// <summary>A unit cube as STL would store it: 12 triangles, every corner repeated.</summary>
    private static List<Vec3[]> Cube()
    {
        var p = new[] { V(0, 0, 0), V(1, 0, 0), V(1, 1, 0), V(0, 1, 0), V(0, 0, 1), V(1, 0, 1), V(1, 1, 1), V(0, 1, 1) };
        int[][] quads = { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 1, 2, 6, 5 }, new[] { 0, 4, 7, 3 } };
        return quads.SelectMany(q => new[] { new[] { p[q[0]], p[q[1]], p[q[2]] }, new[] { p[q[0]], p[q[2]], p[q[3]] } }).ToList();
    }

    [Fact]
    public void Cube_KeepsSharpCorners_AndFindsItsTwelveEdges()
    {
        var mesh = PreviewMesh.Build(Cube());

        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(24, mesh.Positions.Count / 3); // 8 corners x 3 face normals each
        Assert.Equal(12, mesh.EdgeCount);           // the diagonals inside each face aren't edges
    }

    [Fact]
    public void Cylinder_SidesAreSmooth_WithEdgesOnlyAtTheRims()
    {
        const int sides = 32;
        var triangles = new List<Vec3[]>();
        for (var i = 0; i < sides; i++)
        {
            double a = 2 * Math.PI * i / sides, b = 2 * Math.PI * (i + 1) / sides;
            Vec3 p0 = V(Math.Cos(a), Math.Sin(a), 0), p1 = V(Math.Cos(b), Math.Sin(b), 0);
            Vec3 q0 = V(Math.Cos(a), Math.Sin(a), 1), q1 = V(Math.Cos(b), Math.Sin(b), 1);
            triangles.Add(new[] { p0, p1, q1 });
            triangles.Add(new[] { p0, q1, q0 });
            triangles.Add(new[] { V(0, 0, 0), p1, p0 });
            triangles.Add(new[] { V(0, 0, 1), q0, q1 });
        }

        var mesh = PreviewMesh.Build(triangles);

        Assert.Equal(2 * sides, mesh.EdgeCount); // the two rim circles, no lines down the side
        // A side vertex's normal points straight out (smoothed), not along a facet.
        var normal = Enumerable.Range(0, mesh.Positions.Count / 3)
            .Where(i => Math.Abs(mesh.Normals[i * 3 + 2]) < 0.01)
            .Select(i => (X: mesh.Normals[i * 3], Px: mesh.Positions[i * 3], Py: mesh.Positions[i * 3 + 1], Ny: mesh.Normals[i * 3 + 1]))
            .First();
        Assert.Equal(normal.Px, normal.X, 3);
        Assert.Equal(normal.Py, normal.Ny, 3);
    }
}
