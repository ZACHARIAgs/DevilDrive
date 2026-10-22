using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jarvis.Core.Preview;
using Xunit;

namespace Jarvis.Core.Tests;

public class PreviewSimplifierTests
{
    private static Vec3 V(double x, double y, double z) => new Vec3(x, y, z);

    /// <summary>A finely tessellated sphere plus a tiny, very detailed one beside it (like a screw on a big part).</summary>
    private static List<Vec3[]> DenseScene(int rings)
    {
        var triangles = new List<Vec3[]>();
        void Sphere(Vec3 center, double radius, int n)
        {
            Vec3 Point(int i, int j)
            {
                double theta = Math.PI * i / n, phi = 2 * Math.PI * j / n;
                return center + V(Math.Sin(theta) * Math.Cos(phi), Math.Sin(theta) * Math.Sin(phi), Math.Cos(theta)) * radius;
            }
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                {
                    triangles.Add(new[] { Point(i, j), Point(i + 1, j), Point(i + 1, j + 1) });
                    triangles.Add(new[] { Point(i, j), Point(i + 1, j + 1), Point(i, j + 1) });
                }
            }
        }
        Sphere(V(0, 0, 0), 50, rings);
        Sphere(V(80, 0, 0), 2, rings);
        return triangles;
    }

    private static (Vec3 Min, Vec3 Max) Bounds(IEnumerable<Vec3[]> triangles)
    {
        var points = triangles.SelectMany(t => t).ToList();
        return (V(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                V(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
    }

    [Fact]
    public void BigMesh_FitsTheLimit_AndKeepsItsSize()
    {
        var dense = DenseScene(160); // 2 x 51,200 triangles
        var simplified = PreviewSimplifier.Simplify(dense, PreviewSimplifier.MaxTriangles);

        Assert.InRange(simplified.Count, PreviewSimplifier.MaxTriangles / 4, PreviewSimplifier.MaxTriangles);
        var (min, max) = Bounds(dense);
        var (newMin, newMax) = Bounds(simplified);
        Assert.True((newMin - min).Length < 3 && (newMax - max).Length < 3, "the overall shape keeps its size");
        Assert.All(simplified, t => Assert.True(Vec3.Cross(t[1] - t[0], t[2] - t[0]).Length > 0, "no collapsed triangles"));
    }

    [Fact]
    public void SmallMesh_IsLeftAlone()
    {
        var small = DenseScene(20); // 1,600 triangles
        Assert.Same(small[5], PreviewSimplifier.Simplify(small, PreviewSimplifier.MaxTriangles)[5]);
    }

    [Fact]
    public void ShrinkIfLarge_RewritesOnlyBigFiles_AsReadableBinaryStl()
    {
        var folder = Path.Combine(Path.GetTempPath(), "capstone-pdm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var big = Path.Combine(folder, "assembly.stl");
            var small = Path.Combine(folder, "part.stl");
            using (var stream = File.Create(big)) PreviewSimplifier.WriteBinary(stream, DenseScene(160));
            using (var stream = File.Create(small)) PreviewSimplifier.WriteBinary(stream, DenseScene(20));
            var smallBytes = File.ReadAllBytes(small);

            var (before, after) = PreviewSimplifier.ShrinkIfLarge(big);
            var unchanged = PreviewSimplifier.ShrinkIfLarge(small);

            Assert.Equal(102400, before);
            Assert.True(after <= PreviewSimplifier.MaxTriangles);
            Assert.Equal(84 + 50L * after, new FileInfo(big).Length);
            using (var stream = File.OpenRead(big)) Assert.Equal(after, StlReader.Read(stream).Count);
            Assert.Equal(unchanged.Before, unchanged.After);
            Assert.Equal(smallBytes, File.ReadAllBytes(small));
            Assert.Equal(new[] { big, small }.OrderBy(f => f), Directory.GetFiles(folder).OrderBy(f => f)); // no temporary files left
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
