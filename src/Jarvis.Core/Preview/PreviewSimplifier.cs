using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Jarvis.Core.Preview;

/// <summary>
/// Keeps 3D previews small. Every preview is stored in the vault's Git LFS (and downloaded by every
/// teammate), and an assembly's preview holds every part, including purchased hardware with threads
/// and knurling. A preview over <see cref="MaxTriangles"/> is simplified by merging points that are
/// close together (vertex clustering): fine detail disappears first, the overall shape stays.
/// </summary>
public static class PreviewSimplifier
{
    /// <summary>About 600 KB as binary STL. Part previews are usually far below it and stay untouched.</summary>
    public const int MaxTriangles = 12000;

    /// <summary>Rewrites the STL smaller if it has more than <paramref name="maxTriangles"/> triangles. Returns (before, after) triangle counts.</summary>
    public static (int Before, int After) ShrinkIfLarge(string stlPath, int maxTriangles = MaxTriangles)
    {
        List<Vec3[]> triangles;
        using (var stream = File.OpenRead(stlPath))
        {
            triangles = StlReader.Read(stream);
        }
        if (triangles.Count <= maxTriangles) return (triangles.Count, triangles.Count);

        var simplified = Simplify(triangles, maxTriangles);
        var temporary = stlPath + ".simplifying";
        using (var stream = File.Create(temporary))
        {
            WriteBinary(stream, simplified);
        }
        File.Copy(temporary, stlPath, overwrite: true); // the full-size file stays if this fails
        File.Delete(temporary);
        return (triangles.Count, simplified.Count);
    }

    /// <summary>
    /// The same shape with at most <paramref name="maxTriangles"/> triangles (unless even a very coarse
    /// grid can't get there). Snaps every point to a grid, merging the points in each cell into their
    /// average, and drops triangles that collapse or repeat. The grid gets coarser until the mesh fits.
    /// </summary>
    public static List<Vec3[]> Simplify(IReadOnlyList<Vec3[]> triangles, int maxTriangles)
    {
        if (triangles.Count <= maxTriangles) return triangles.ToList();

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var point in triangles.SelectMany(t => t))
        {
            minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y); minZ = Math.Min(minZ, point.Z);
            maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y); maxZ = Math.Max(maxZ, point.Z);
        }
        var origin = new Vec3(minX, minY, minZ);
        var diagonal = (new Vec3(maxX, maxY, maxZ) - origin).Length;
        if (diagonal <= 0) return triangles.ToList();

        var result = triangles.ToList();
        for (var cells = 512; cells >= 8; cells = cells * 4 / 5)
        {
            result = Cluster(triangles, origin, diagonal / cells);
            if (result.Count <= maxTriangles) break;
        }
        return result;
    }

    private static List<Vec3[]> Cluster(IReadOnlyList<Vec3[]> triangles, Vec3 origin, double cell)
    {
        long KeyOf(Vec3 p)
        {
            // 21 bits per axis is plenty: the grid has at most 512 cells along the diagonal.
            var x = (long)Math.Floor((p.X - origin.X) / cell);
            var y = (long)Math.Floor((p.Y - origin.Y) / cell);
            var z = (long)Math.Floor((p.Z - origin.Z) / cell);
            return (x << 42) | (y << 21) | z;
        }

        // Each cell's point is the average of the points in it, so surfaces stay where they were.
        var sums = new Dictionary<long, (double X, double Y, double Z, int Count)>();
        foreach (var point in triangles.SelectMany(t => t))
        {
            var key = KeyOf(point);
            sums.TryGetValue(key, out var sum);
            sums[key] = (sum.X + point.X, sum.Y + point.Y, sum.Z + point.Z, sum.Count + 1);
        }

        var result = new List<Vec3[]>();
        var seen = new HashSet<(long, long, long)>();
        foreach (var triangle in triangles)
        {
            var a = KeyOf(triangle[0]);
            var b = KeyOf(triangle[1]);
            var c = KeyOf(triangle[2]);
            if (a == b || b == c || a == c) continue; // collapsed to a line or a point
            var sorted = new[] { a, b, c };
            Array.Sort(sorted);
            if (!seen.Add((sorted[0], sorted[1], sorted[2]))) continue; // same triangle as one already kept
            result.Add(new[] { Average(sums[a]), Average(sums[b]), Average(sums[c]) });
        }
        return result;
    }

    private static Vec3 Average((double X, double Y, double Z, int Count) sum) =>
        new Vec3(sum.X / sum.Count, sum.Y / sum.Count, sum.Z / sum.Count);

    /// <summary>Binary STL: 80-byte header, triangle count, then normal + 3 points + 2 spare bytes per triangle.</summary>
    public static void WriteBinary(Stream stream, IReadOnlyList<Vec3[]> triangles)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var header = new byte[80];
        var text = Encoding.ASCII.GetBytes("Jarvis 3D preview (simplified)");
        Array.Copy(text, header, text.Length);
        writer.Write(header);
        writer.Write((uint)triangles.Count);
        foreach (var t in triangles)
        {
            var normal = Vec3.Cross(t[1] - t[0], t[2] - t[0]);
            normal = normal.Length > 0 ? normal.Normalized() : normal;
            foreach (var v in new[] { normal, t[0], t[1], t[2] })
            {
                writer.Write((float)v.X);
                writer.Write((float)v.Y);
                writer.Write((float)v.Z);
            }
            writer.Write((ushort)0);
        }
    }
}
